using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Mapping;
using waabe_navi_shared;

namespace waabe_navi_mcp_server.Services
{
    public sealed class MCPServer : IDisposable
    {
        private readonly int _port;
        private readonly HttpListener _listener = new HttpListener();
        // ✅ FIX: _jss supprimé — JsonConvert est statique
        private readonly RpcRouter _router;
        private CancellationTokenSource _cts;
        private Task _acceptLoopTask;

        public event EventHandler<string> ServerMessage;
        public event EventHandler<Exception> ServerError;
        public bool IsRunning { get; private set; }
        public int Port => _port;
        public string ServerUrl => $"http://127.0.0.1:{_port}/";

        public MCPServer(int port)
        {
            _port = port;
            _router = new RpcRouter();
            try
            {
                RpcMap.Register(_router.Routes);
            }
            catch (Exception ex)
            {
                LogHelper.LogEvent($"[MCPServer] Warnung: RpcMap.Register: {ex.Message}");
            }
        }

        public async Task<bool> StartAsync()
        {
            if (IsRunning) return true;
            try
            {
                var urlA = $"http://127.0.0.1:{_port}/";
                var urlB = $"http://localhost:{_port}/";
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(urlA);
                if (!string.Equals(urlA, urlB, StringComparison.OrdinalIgnoreCase))
                    _listener.Prefixes.Add(urlB);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
                IsRunning = true;
                OnServerMessage($"Listening on: {urlA} (and localhost)");
                return true;
            }
            catch (HttpListenerException hex)
            {
                IsRunning = false;
                OnServerError(hex);
                OnServerMessage("Prüfe URL-ACL (netsh) oder ob ein anderer Prozess den Port belegt.");
                return false;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                OnServerError(ex);
                return false;
            }
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            try
            {
                _cts?.Cancel();
                try { _listener.Stop(); } catch { }
                if (_acceptLoopTask != null)
                    try { await _acceptLoopTask.ConfigureAwait(false); } catch { }
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(ctx), ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { if (ct.IsCancellationRequested) break; }
                catch (Exception ex) { OnServerError(ex); }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";

            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
                var method = req.HttpMethod?.ToUpperInvariant();

                if (method == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

                if (method == "GET" && (path == "" || path == "/"))
                {
                    await WriteTextAsync(res, 200, "WAABE MCP Server is running. Try POST /rpc");
                    return;
                }

                if (method == "GET" && path == "/health")
                {
                    await WriteTextAsync(res, 200, "OK");
                    return;
                }

                if (method == "GET" && path == "/manifest")
                {
                    var baseUrl = GetBaseUrl(req);
                    var doc = ManifestBuilder.Build(baseUrl);
                    ManifestBuilder.TryWriteToDisk(doc, out _);
                    // ✅ FIX: JsonConvert.SerializeObject remplace _jss.Serialize
                    var json = JsonConvert.SerializeObject(doc);
                    await WriteRawAsync(res, 200, "application/json; charset=utf-8", json);
                    return;
                }

                if (method == "GET" && path == "/mcp_manifest.json")
                {
                    var baseUrl = GetBaseUrl(req);
                    var doc = ManifestBuilder.Build(baseUrl);
                    ManifestBuilder.TryWriteToDisk(doc, out var fullPath);
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    {
                        var fileJson = File.ReadAllText(fullPath, Encoding.UTF8);
                        await WriteRawAsync(res, 200, "application/json; charset=utf-8", fileJson);
                    }
                    else
                    {
                        // ✅ FIX
                        var json = JsonConvert.SerializeObject(doc);
                        await WriteRawAsync(res, 200, "application/json; charset=utf-8", json);
                    }
                    return;
                }

                if (method == "POST" && (path == "/rpc" || path == ""))
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(body))
                    {
                        await WriteJsonAsync(res, 400, new { error = "empty body" });
                        return;
                    }

                    RpcRequest rpcReq;
                    try
                    {
                        // ✅ FIX: JsonConvert remplace _jss.Deserialize
                        rpcReq = JsonConvert.DeserializeObject<RpcRequest>(body);
                    }
                    catch (Exception dex)
                    {
                        await WriteJsonAsync(res, 400, new { error = "invalid json", detail = dex.Message });
                        return;
                    }

                    object rpcResp;
                    try
                    {
                        rpcResp = _router.Dispatch(rpcReq);
                    }
                    catch (Exception ex)
                    {
                        OnServerError(ex);
                        rpcResp = RpcResponse<object>.Fail("NVX_INTERNAL", ex.Message);
                    }

                    // ✅ FIX
                    var rpcJson = JsonConvert.SerializeObject(rpcResp);
                    await WriteRawAsync(res, 200, "application/json; charset=utf-8", rpcJson);
                    return;
                }

                await WriteTextAsync(ctx.Response, 404, "Not Found");
            }
            catch (Exception ex)
            {
                OnServerError(ex);
                try { await WriteJsonAsync(ctx.Response, 500, new { error = ex.Message }); } catch { }
            }
        }

        private static string GetBaseUrl(HttpListenerRequest req)
        {
            try
            {
                var scheme = req.Url?.Scheme ?? "http";
                var host = req.UserHostName ?? "127.0.0.1";
                var port = req.Url?.Port ?? 80;
                return $"{scheme}://{host}:{port}/";
            }
            catch { return "http://127.0.0.1/"; }
        }

        private static async Task WriteTextAsync(HttpListenerResponse res, int code, string text)
            => await WriteRawAsync(res, code, "text/plain; charset=utf-8", text);

        private static async Task WriteJsonAsync(HttpListenerResponse res, int code, object obj)
        {
            // ✅ FIX: JsonConvert remplace new JavaScriptSerializer()
            var json = JsonConvert.SerializeObject(obj);
            await WriteRawAsync(res, code, "application/json; charset=utf-8", json);
        }

        private static async Task WriteRawAsync(HttpListenerResponse res, int code, string contentType, string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
            res.StatusCode = code;
            res.ContentType = contentType;
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = bytes.LongLength;
            using (var os = res.OutputStream)
                await os.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private void OnServerMessage(string msg)
        {
            try { ServerMessage?.Invoke(this, msg); } catch { }
            LogHelper.LogEvent($"[MCP] {msg}");
        }

        private void OnServerError(Exception ex)
        {
            try { ServerError?.Invoke(this, ex); } catch { }
            LogHelper.LogEvent($"[MCP-ERROR] {ex.GetType().Name}: {ex.Message}");
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Close(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }
    }
}