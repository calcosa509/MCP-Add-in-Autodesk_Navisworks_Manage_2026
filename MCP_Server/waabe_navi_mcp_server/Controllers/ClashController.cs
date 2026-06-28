using System.Threading;
using Newtonsoft.Json;  // ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦ FIX: remplace System.Web.Script.Serialization
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services.Backends;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;

namespace waabe_navi_mcp_server.Controllers
{
    public sealed class ClashController
    {
        // ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦ FIX: supprimÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â© ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â JsonConvert est statique, pas besoin d'instance

        public object RunSimpleClash(RpcRequest req)
        {
            return Wrap(() =>
            {
                // ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã¢â‚¬Å“ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦ FIX: JsonConvert.DeserializeObject remplace _jss.ConvertToType
                var args = JsonConvert.DeserializeObject<ClashRunArgs>(
                               JsonConvert.SerializeObject(req.@params));

                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;

                var dto = BackendResolver.Instance.RunClashAsync(args, ct)
                           .GetAwaiter().GetResult();

                return RpcResponse<ClashSummaryDto>.Success(dto);
            });
        }
        public object RunClashAllModels(RpcRequest req)
        {
            return Wrap(() => {
                var backend = BackendResolver.Instance;
                var overviewCt = new CancellationTokenSource(30000).Token;
                var overview = backend.GetModelOverviewAsync(overviewCt).GetAwaiter().GetResult();
                var models = overview.Models;
                var pairs = new System.Collections.Generic.List<ClashModelPairDto>();
                int total = 0;
                for (int i = 0; i < models.Count; i++)
                    for (int j = i + 1; j < models.Count; j++)
                    {
                        var clashCt = new CancellationTokenSource(180000).Token;
                        var args = new ClashRunArgs { scopeA = models[i].canonical_id, scopeB = models[j].canonical_id };
                        var result = backend.RunClashAsync(args, clashCt).GetAwaiter().GetResult();
                        pairs.Add(new ClashModelPairDto { model_a = models[i].FileName, model_b = models[j].FileName, clash_count = result.results, success = result.success });
                        total += result.results;
                    }
                return RpcResponse<ClashAllDto>.Success(new ClashAllDto { total_clashes = total, pairs_tested = pairs.Count,

pairs = pairs, success = true, message = total + " clashes found in " + pairs.Count + " pairs" });
            });
        }
    }
}
