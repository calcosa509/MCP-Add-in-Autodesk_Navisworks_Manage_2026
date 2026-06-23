using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;          // ✅ FIX: remplace JavaScriptSerializer
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Mapping;
using waabe_navi_shared;

namespace waabe_navi_mcp_server.Infrastructure
{
    public static class ManifestBuilder
    {
        // ✅ FIX: _ser supprimé — JsonConvert est statique, pas besoin d'instance

        public static ManifestDoc Build(string baseUrl)
        {
            var routes = RpcMap.BuildRoutes().Keys
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => new ManifestRoute { name = k })
                .ToList();

            var capabilities = new Dictionary<string, object>
            {
                ["transport"] = "http",
                ["rpc_path"] = "/rpc",
                ["health"] = "/health"
            };

            var doc = new ManifestDoc
            {
                name = "waabe_navi_mcp_server",
                version = "1.0.0",
                generated_at_utc = DateTime.UtcNow,
                base_url = NormalizeBaseUrl(baseUrl),
                endpoints = new ManifestEndpoints
                {
                    rpc = Combine(NormalizeBaseUrl(baseUrl), "rpc"),
                    health = Combine(NormalizeBaseUrl(baseUrl), "health"),
                    manifest = Combine(NormalizeBaseUrl(baseUrl), "manifest")
                },
                routes = routes,
                capabilities = capabilities
            };

            return doc;
        }

        public static bool TryWriteToDisk(ManifestDoc doc, out string fullPath)
        {
            try
            {
                var contentDir = GetDefaultContentDirectory();
                Directory.CreateDirectory(contentDir);
                fullPath = Path.Combine(contentDir, "mcp_manifest.json");
                // ✅ FIX: JsonConvert.SerializeObject remplace _ser.Serialize
                var json = JsonConvert.SerializeObject(doc, Formatting.Indented);
                File.WriteAllText(fullPath, json);
                LogHelper.LogEvent($"[Manifest] mcp_manifest.json written: {fullPath}");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.LogEvent($"[Manifest] Write failed: {ex.Message}");
                fullPath = null;
                return false;
            }
        }

        public static string GetDefaultContentDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Autodesk", "ApplicationPlugins",
                                "waabe_navi_mcp.bundle", "Contents", "v23");
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
            return baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        }

        private static string Combine(string baseUrl, string rel)
        {
            if (string.IsNullOrEmpty(baseUrl)) return "/" + rel.TrimStart('/');
            return baseUrl.TrimEnd('/') + "/" + rel.TrimStart('/');
        }
    }

    public sealed class ManifestDoc
    {
        public string name { get; set; }
        public string version { get; set; }
        public string base_url { get; set; }
        public DateTime generated_at_utc { get; set; }
        public ManifestEndpoints endpoints { get; set; }
        public List<ManifestRoute> routes { get; set; }
        public Dictionary<string, object> capabilities { get; set; }
    }

    public sealed class ManifestEndpoints
    {
        public string rpc { get; set; }
        public string health { get; set; }
        public string manifest { get; set; }
    }

    public sealed class ManifestRoute
    {
        public string name { get; set; }
    }
}