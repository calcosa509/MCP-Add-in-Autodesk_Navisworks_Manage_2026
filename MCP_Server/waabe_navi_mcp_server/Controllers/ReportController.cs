// Controllers/ReportController.cs
using System.Threading;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services.Backends;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;
namespace waabe_navi_mcp_server.Controllers
{
    public sealed class ReportController
    {
        public object GenerateModelReport(RpcRequest req)
        {
            return Wrap(() => {
                var backend = BackendResolver.Instance;
                var ct180 = new CancellationTokenSource(180000).Token;
                var ct30 = new CancellationTokenSource(30000).Token;

                // 1. Model overview
                var overview = backend.GetModelOverviewAsync(ct30).GetAwaiter().GetResult();

                // 2. Clash all models
                var models = overview.Models;
                var pairs = new System.Collections.Generic.List<ClashModelPairDto>();
                int totalClashes = 0;
                for (int i = 0; i < models.Count; i++)
                    for (int j = i + 1; j < models.Count; j++)
                    {
                        var clashCt = new CancellationTokenSource(90000).Token;
                        var args = new ClashRunArgs { scopeA = models[i].canonical_id, scopeB = models[j].canonical_id };
                        var result = backend.RunClashAsync(args, clashCt).GetAwaiter().GetResult();
                        pairs.Add(new ClashModelPairDto { model_a = models[i].FileName, model_b = models[j].FileName, clash_count = result.results, success = result.success });
                        totalClashes += result.results;
                    }

                // 3. Viewpoints
                var viewpoints = backend.ListViewpointsAsync(ct30).GetAwaiter().GetResult();

                // 4. Build report
                var report = new ModelReportDto
                {
                    document_title = overview.DocumentTitle,
                    models_count = overview.ModelsCount,
                    total_elements = overview.TotalElements,
                    models = overview.Models,
                    clash_report = new ClashAllDto { total_clashes = totalClashes, pairs_tested = pairs.Count, pairs = pairs, success = true, message = totalClashes + " clashes in " + pairs.Count + " pairs" },
                    viewpoints = viewpoints,
                    generated_at = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    success = true,
                    message = "Report generated"
                };
                return RpcResponse<ModelReportDto>.Success(report);
            });
        }
    }
}