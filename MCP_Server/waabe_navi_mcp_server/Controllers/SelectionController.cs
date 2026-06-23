using System;
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
    public sealed class SelectionController
    {
        private readonly ISelectionService _svc = new SelectionService();
        // ✅ FIX: _jss supprimé — JsonConvert est statique

        public object ClearSelection(RpcRequest req)
        {
            return Wrap(() =>
            {
                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.ClearSelectionAsync(cts.Token).GetAwaiter().GetResult();
                return RpcResponse<ApplyResultDto>.Success(dto);
            });
        }

        public object GetCurrentSelectionSnapshot(RpcRequest req)
        {
            return Wrap(() =>
            {
                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.GetSnapshotAsync(cts.Token).GetAwaiter().GetResult();
                return RpcResponse<SelectionSnapshotDto>.Success(dto);
            });
        }

        public object ApplySelection(RpcRequest req)
        {
            return Wrap(() =>
            {
                // ✅ FIX: JsonConvert remplace _jss.ConvertToType
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                               JsonConvert.SerializeObject(req.@params ?? new object()));

                var ids = new List<string>();
                if (dict != null && dict.TryGetValue("canonical_id", out var raw))
                {
                    // ✅ FIX: Newtonsoft désérialise les tableaux JSON en JArray
                    var arr = raw as Newtonsoft.Json.Linq.JArray;
                    if (arr != null)
                        foreach (var x in arr)
                        {
                            var s = x?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                        }
                }

                bool keep = true;
                if (dict != null && dict.TryGetValue("keepExistingSelection", out var k))
                    if (k is bool b) keep = b;
                    else if (bool.TryParse(k?.ToString(), out var bp)) keep = bp;

                if (ids.Count == 0)
                    throw new ArgumentException("canonical_id[] (string) required.");

                var cts = new CancellationTokenSource(Settings.DefaultTimeoutMs);
                var dto = _svc.ApplySelectionAsync(ids, keep, cts.Token).GetAwaiter().GetResult();
                return RpcResponse<List<SimpleItemRef>>.Success(dto);
            });
        }
    }
}