// Controllers/ExportController.cs
using System.Threading;
using Newtonsoft.Json;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services;
using waabe_navi_mcp_server.Services.Implementations;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;

namespace waabe_navi_mcp_server.Controllers
{
    public sealed class ExportController
    {
        private readonly IExportService _svc = new ExportService();

        public object GetModelInfo(RpcRequest request)
        {
            return Waabe.Navisworks.Bridge.Commands.GetModelInfoCommand.Execute();
        }
    }
}