// waabe_navi_mcpserver/Controllers/VisibilityController.cs
using System.Threading;
using Newtonsoft.Json;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services;
using waabe_navi_mcp_server.Services.Backends;
using waabe_navi_mcp_server.Services.Implementations;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;
namespace waabe_navi_mcp_server.Controllers
{
    public sealed class VisibilityController
    {
        public object SaveViewpoint(RpcRequest req)
        {
            return Wrap(() => {
                var name = (req.@params as Newtonsoft.Json.Linq.JObject)?["name"]?.ToString() ?? "";
                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;
                var dto = BackendResolver.Instance.SaveViewpointAsync(name, ct).GetAwaiter().GetResult();
                return RpcResponse<ViewpointDto>.Success(dto);
            });
        }
        public object ListViewpoints(RpcRequest req)
        {
            return Wrap(() => {
                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;
                var dto = BackendResolver.Instance.ListViewpointsAsync(ct).GetAwaiter().GetResult();
                return RpcResponse<ViewpointListDto>.Success(dto);
            });
        }
        public object ActivateViewpoint(RpcRequest req)
        {
            return Wrap(() => {
                var name = (req.@params as Newtonsoft.Json.Linq.JObject)?["name"]?.ToString() ?? "";
                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;
                var dto = BackendResolver.Instance.ActivateViewpointAsync(name, ct).GetAwaiter().GetResult();
                return RpcResponse<ViewpointDto>.Success(dto);
            });
        }
        public object HideElements(RpcRequest req)
        {
            return Wrap(() => {
                var scope = (req.@params as Newtonsoft.Json.Linq.JObject)?["scope"]?.ToString() ?? "all";
                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;
                var dto = BackendResolver.Instance.HideElementsAsync(scope, ct).GetAwaiter().GetResult();
                return RpcResponse<HideElementsDto>.Success(dto);
            });
        }
        public object ShowElements(RpcRequest req)
        {
            return Wrap(() => {
                var scope = (req.@params as Newtonsoft.Json.Linq.JObject)?["scope"]?.ToString() ?? "all";
                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;
                var dto = BackendResolver.Instance.ShowElementsAsync(scope, ct).GetAwaiter().GetResult();
                return RpcResponse<HideElementsDto>.Success(dto);
            });
        }
        public object ExportCurrentView(RpcRequest req)
        {
            return Wrap(() => {
                var p = req.@params as Newtonsoft.Json.Linq.JObject;
                int w = p?["width"] != null ? (int)p["width"] : 1280;
                int h = p?["height"] != null ? (int)p["height"] : 720;
                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;
                var dto = BackendResolver.Instance.ExportCurrentViewAsync(w, h, ct).GetAwaiter().GetResult();
                return RpcResponse<ExportViewDto>.Success(dto);
            });
        }
    }
}
