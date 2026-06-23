// Controllers/SearchController.cs
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services;
using waabe_navi_mcp_server.Services.Implementations;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;

namespace waabe_navi_mcp_server.Controllers
{
    public sealed class SearchController
    {
        private readonly ISearchService _svc = new SearchService();
        // ✅ FIX: _jss supprimé — JsonConvert est statique

        public object GetCountByCategory(RpcRequest req)
        {
            return Wrap(() =>
            {
                // ✅ FIX: JsonConvert remplace _jss.ConvertToType
                var q = JsonConvert.DeserializeObject<CategoryQuery>(
                            JsonConvert.SerializeObject(req.@params));
                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.GetCountByCategoryAsync(q, cts.Token).GetAwaiter().GetResult();
                return RpcResponse<ElementCountDto>.Success(dto);
            });
        }

        public object ListItemsToProperty(RpcRequest req)
        {
            return Wrap(() =>
            {
                var args = JsonConvert.DeserializeObject<ListItemsToPropertyArgs>(
                                JsonConvert.SerializeObject(req.@params));
                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.ListItemsToPropertyAsync(args, cts.Token).GetAwaiter().GetResult();
                return RpcResponse<PropertyItemListDto>.Success(dto);
            });
        }

        public object ListPropertiesForItem(RpcRequest req)
        {
            return Wrap(() =>
            {
                var p = JsonConvert.DeserializeObject<ItemRef>(
                            JsonConvert.SerializeObject(req.@params));
                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.GetItemPropertiesAsync(p.item_id, cts.Token).GetAwaiter().GetResult();
                return RpcResponse<ItemPropertiesDto>.Success(dto);
            });
        }
    }
}