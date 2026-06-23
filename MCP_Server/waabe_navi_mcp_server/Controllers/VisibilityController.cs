// waabe_navi_mcpserver/Controllers/VisibilityController.cs
using System.Threading;
using Newtonsoft.Json;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_mcp_server.Infrastructure;
using waabe_navi_mcp_server.Services;
using waabe_navi_mcp_server.Services.Implementations;
using static waabe_navi_mcp_server.Infrastructure.ErrorHandlingMiddleware;

namespace waabe_navi_mcp_server.Controllers
{
    public sealed class VisibilityController
    {
        private readonly IVisibilityService _svc = new VisibilityService();
        // ✅ FIX: _jss supprimé — JsonConvert est statique

    }
}