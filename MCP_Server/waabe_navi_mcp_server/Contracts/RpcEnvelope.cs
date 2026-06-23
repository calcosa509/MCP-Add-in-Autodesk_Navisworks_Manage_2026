using System;
using Newtonsoft.Json;  // ✅ FIX: remplace System.Web.Script.Serialization

namespace waabe_navi_mcp_server.Contracts
{
    public sealed class RpcRequest
    {
        public string id { get; set; }
        public string method { get; set; }
        public object @params { get; set; }

        // ✅ FIX: JsonConvert.DeserializeObject remplace JavaScriptSerializer
        public static RpcRequest FromJson(string json)
            => JsonConvert.DeserializeObject<RpcRequest>(json);
    }

    public sealed class RpcMeta
    {
        public string model_revision { get; set; }
        public int query_ms { get; set; }
        public string server_version { get; set; }
        public string request_id { get; set; }
    }

    public sealed class RpcError
    {
        public string code { get; set; }
        public string msg { get; set; }
    }

    public static class RpcResponse
    {
        // ✅ FIX: JsonConvert.SerializeObject remplace JavaScriptSerializer
        public static string ToJson(object obj)
            => JsonConvert.SerializeObject(obj);
    }

    public sealed class RpcResponse<T>
    {
        public bool ok { get; set; }
        public T data { get; set; }
        public RpcError error { get; set; }
        public RpcMeta meta { get; set; }

        public static RpcResponse<T> Success(T data, RpcMeta meta = null)
            => new RpcResponse<T> { ok = true, data = data, meta = meta };

        public static RpcResponse<T> Fail(string code, string msg)
            => new RpcResponse<T> { ok = false, error = new RpcError { code = code, msg = msg } };
    }
}