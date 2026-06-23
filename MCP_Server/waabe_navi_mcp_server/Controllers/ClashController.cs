using System.Threading;
using Newtonsoft.Json;  // ✅ FIX: remplace System.Web.Script.Serialization
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services.Backends;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;

namespace waabe_navi_mcp_server.Controllers
{
    public sealed class ClashController
    {
        // ✅ FIX: supprimé — JsonConvert est statique, pas besoin d'instance

        public object RunSimpleClash(RpcRequest req)
        {
            return Wrap(() =>
            {
                // ✅ FIX: JsonConvert.DeserializeObject remplace _jss.ConvertToType
                var args = JsonConvert.DeserializeObject<ClashRunArgs>(
                               JsonConvert.SerializeObject(req.@params));

                var ct = new CancellationTokenSource(Settings.DefaultTimeoutMs).Token;

                var dto = BackendResolver.Instance.RunClashAsync(args, ct)
                           .GetAwaiter().GetResult();

                return RpcResponse<ClashSummaryDto>.Success(dto);
            });
        }
    }
}