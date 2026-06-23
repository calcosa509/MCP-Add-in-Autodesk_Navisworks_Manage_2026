// Controllers/ModelController.cs
using System.Threading;
using Newtonsoft.Json;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services;
using waabe_navi_mcp_server.Services.Implementations;
using waabe_navi_shared;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;

namespace waabe_navi_mcp_server.Controllers
{
    public sealed class ModelController
    {
        private readonly IModelQueryService _svc = new ModelQueryService();

        public object GetModelOverview(RpcRequest req)
        {
            using (LoggingExtensions.Scope("get_model_overview"))
            {
                return Wrap(() =>
                {
                    var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                    var dto = _svc.GetOverviewAsync(cts.Token).GetAwaiter().GetResult();
                    return RpcResponse<ModelOverviewDto>.Success(dto);
                });
            }
        }

        public object GetUnits(RpcRequest req)
        {
            return Wrap(() =>
            {
                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.GetUnitsAsync(cts.Token).GetAwaiter().GetResult();
                return RpcResponse<UnitInfoDto>.Success(dto);
            });
        }

        public object GetPropertyDistributionByCategory(RpcRequest req)
        {
            using (LoggingExtensions.Scope("get_property_distribution_by_category"))
            {
                return Wrap(() =>
                {
                    var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                    var dto = _svc.GetPropertyDistributionByCategoryAsync(cts.Token).GetAwaiter().GetResult();
                    return RpcResponse<ElementCountDto>.Success(dto);
                });
            }
        }
    }
}