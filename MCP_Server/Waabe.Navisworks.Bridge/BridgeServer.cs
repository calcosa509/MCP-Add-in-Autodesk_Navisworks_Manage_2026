using Newtonsoft.Json;
using System;
using System.Linq;          // ✅ FIX: nécessaire pour .Count() sur ModelItemEnumerableCollection
using System.Net;
using System.Text;
using System.Threading;

namespace Waabe.Navisworks.Bridge
{
    public static class BridgeServer
    {
        private static HttpListener _listener;
        private static bool _started = false;
        private static readonly object _lock = new object();

        public static void Start()
        {
            if (_started) return;

            lock (_lock)
            {
                if (_started) return;

                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:5050/");
                _listener.Start();
                _started = true;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    while (_listener.IsListening)
                    {
                        try
                        {
                            var context = _listener.GetContext();
                            ThreadPool.QueueUserWorkItem(
                                state => HandleRequest((HttpListenerContext)state), context);
                        }
                        catch { }
                    }
                });
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (!_started) return;
                _started = false;
                _listener?.Stop();
                _listener?.Close();
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath.ToLowerInvariant();
                string jsonResponse;

                switch (path)
                {
                    case "/model/info":
                        jsonResponse = GetModelInfo();
                        break;

                    case "/model/items/count":
                        jsonResponse = GetItemCount();
                        break;

                    default:
                        context.Response.StatusCode = 404;
                        jsonResponse = JsonConvert.SerializeObject(
                            new { error = "Endpoint inconnu", path = path });
                        break;
                }

                byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        private static string GetModelInfo()
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null)
                return JsonConvert.SerializeObject(
                    new { error = "Aucun document Navisworks actif" });

            return JsonConvert.SerializeObject(new
            {
                fileName = doc.FileName ?? "Untitled",
                currentFileName = doc.CurrentFileName,
                modelCount = doc.Models.Count,
                timestamp = DateTime.UtcNow
            });
        }

        private static string GetItemCount()
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null)
                return JsonConvert.SerializeObject(
                    new { error = "Aucun document Navisworks actif" });

            long totalItems = 0;

            foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
            {
                if (model?.RootItem != null)
                {
                    // ✅ FIX: .Cast<ModelItem>().Count() via System.Linq
                    totalItems += model.RootItem.DescendantsAndSelf
                                       .Cast<Autodesk.Navisworks.Api.ModelItem>()
                                       .Count();
                }
            }

            return JsonConvert.SerializeObject(new
            {
                totalItems = totalItems,
                modelCount = doc.Models.Count
            });
        }
    }
}