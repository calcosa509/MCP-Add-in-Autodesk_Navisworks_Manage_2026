using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

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

                ThreadPool.QueueUserWorkItem(new WaitCallback(delegate(object _) {
                    while (_listener.IsListening)
                    {
                        try
                        {
                            var context = _listener.GetContext();
                            ThreadPool.QueueUserWorkItem(new WaitCallback(HandleRequest), context);
                        }
                        catch { }
                    }
                }));
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (!_started) return;
                _started = false;
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
        }

        private static void HandleRequest(object state)
        {
            HttpListenerContext context = (HttpListenerContext)state;
            try
            {
                string path = context.Request.Url.AbsolutePath.ToLower();
                string jsonResponse;

                if (path == "/model/info")
                    jsonResponse = GetModelInfo();
                else if (path == "/model/items/count")
                    jsonResponse = GetItemCount();
                else
                {
                    context.Response.StatusCode = 404;
                    jsonResponse = "{\"error\":\"Endpoint inconnu\"}";
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
                return "{\"error\":\"Aucun document Navisworks actif\"}";

            var data = new
            {
                fileName = doc.FileName != null ? doc.FileName : "Untitled",
                currentFileName = doc.CurrentFileName,
                modelCount = doc.Models.Count,
                timestamp = DateTime.UtcNow.ToString()
            };

            return JsonConvert.SerializeObject(data);
        }

        private static string GetItemCount()
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null)
                return "{\"error\":\"Aucun document Navisworks actif\"}";

            long totalItems = 0;
            foreach (Autodesk.Navisworks.Api.Model model in doc.Models)
            {
                if (model != null && model.RootItem != null)
                {
                    totalItems += model.RootItem.DescendantsAndSelf.Count();
                }
            }

            var data = new
            {
                totalItems = totalItems,
                modelCount = doc.Models.Count
            };

            return JsonConvert.SerializeObject(data);
        }
    }
}