using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Controls;
using Autodesk.Navisworks.Api.DocumentParts;
using Autodesk.Navisworks.Api.Interop;
using Autodesk.Navisworks.Api.Interop.ComApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using waabe_navi_mcp_server.Contracts;
using waabe_navi_shared;
using ComApi = Autodesk.Navisworks.Api.Interop.ComApi;

namespace waabe_navi_mcp_server.Services.Backends
{
    public sealed class FallbackBackend : IWaabeNavisworksBackend
    {
        private const string DEFAULT_UNNAMED = "(unbenannt)";
        private const string DEFAULT_CATEGORY = "(Kategorie)";
        private const string DEFAULT_PROPERTY = "(Property)";

        private Selection BuildSelectionFromCanonicalTokens(
            Document doc,
            List<string> tokens,
            out int itemCount,
            out List<ScopeAppliedInfo> infos)
        {
            var col = new ModelItemCollection();
            infos = new List<ScopeAppliedInfo>();
            itemCount = 0;

            if (doc == null || tokens == null || tokens.Count == 0)
                return new Selection(col);

            var items = ResolveItemsByCanonicalIds(doc, tokens);
            if (items == null || items.Count == 0)
                return new Selection(col);

            foreach (var it in items)
            {
                if (it == null) continue;

                var inputId = GetCanonicalId(it);
                var name = SafeNameOrDefault(it.DisplayName, it.ClassDisplayName ?? it.ClassName, DEFAULT_UNNAMED);

                var (targets, reason, steps) = ResolveGeometricTargets(it);

                // FIX: si pas de géométrie trouvée (ex: racine de modèle),
                // expand tous les descendants directement
                if (targets.Count == 0)
                {
                    var desc = it.DescendantsAndSelf?.ToList();
                    if (desc != null && desc.Count > 0)
                    {
                        targets = desc;
                        reason = "expanded:model-root-descendants";
                    }
                }

                var appliedIds = new List<string>();
                foreach (var t in targets)
                {
                    col.Add(t);
                    appliedIds.Add(GetCanonicalId(t));
                }

                infos.Add(new ScopeAppliedInfo
                {
                    input_id = inputId,
                    resolved_id = inputId,
                    applied_id = string.Join(";", appliedIds),
                    reason = (steps > 0 && reason == "ok") ? "promoted:no-geometry" : reason,
                    element_name = name
                });
            }

            itemCount = col.Count;
            return new Selection(col);
        }

        private int CountClashResultsSafe(Autodesk.Navisworks.Api.Clash.ClashTest test)
        {
            if (test == null)
            {
                LogHelper.LogWarning("[CLASH] CountClashResultsSafe: test is null");
                return 0;
            }
            try
            {
                int count = 0;
                var children = test.Children;
                if (children == null)
                {
                    LogHelper.LogEvent("[CLASH] CountClashResultsSafe: children=null");
                    return 0;
                }

                var top = children.ToList();
                foreach (Autodesk.Navisworks.Api.SavedItem child in top)
                {
                    if (child is Autodesk.Navisworks.Api.Clash.ClashResult)
                    {
                        count++;
                        continue;
                    }
                    if (child is Autodesk.Navisworks.Api.Clash.ClashResultGroup g)
                    {
                        var sub = g.Children;
                        if (sub == null) continue;
                        foreach (Autodesk.Navisworks.Api.SavedItem c in sub.ToList())
                        {
                            if (c is Autodesk.Navisworks.Api.Clash.ClashResult) count++;
                        }
                    }
                }

                LogHelper.LogEvent($"[CLASH] CountClashResultsSafe => {count}");
                return count;
            }
            catch (ObjectDisposedException ode)
            {
                LogHelper.LogWarning($"[CLASH] CountClashResultsSafe: ObjectDisposed → 0 ({ode.Message})");
                return 0;
            }
            catch (Exception ex)
            {
                LogHelper.LogWarning($"[CLASH] CountClashResultsSafe: exception → 0 ({ex.Message})");
                return 0;
            }
        }

        public async Task<ClashSummaryDto> RunClashAsync(ClashRunArgs args, CancellationToken ct)
        {
            var dto = new ClashSummaryDto { success = true, test_name = args?.test_name ?? "MCP API Test", results = 0 };

            LogHelper.LogEvent("[CLASH][NET] RunClashAsync ENTER");

            if (args == null)
                return new ClashSummaryDto { success = false, test_name = "MCP API Test", results = 0, message = "INPUT_VALIDATE;Fail;reason=args null" };

            LogHelper.LogEvent($"[CLASH][NET] ARGS scopeA='{args.scopeA}', scopeB='{args.scopeB}', tol={args.tolerance_m.ToString(CultureInfo.InvariantCulture)}");

            try
            {
                dto = await UiThread.InvokeAsync(() =>
                {
                    var doc = Application.MainDocument;
                    if (doc == null)
                        throw new InvalidOperationException("INPUT_VALIDATE;Fail;reason=no active document");

                    int cntA, cntB; bool expA, expB;
                    List<ScopeAppliedInfo> infosA, infosB;

                    var selA = BuildSelectionWithPromotionIfCanonical(doc, args.scopeA ?? "all", out cntA, out expA, out infosA);
                    var selB = BuildSelectionWithPromotionIfCanonical(doc, args.scopeB ?? "all", out cntB, out expB, out infosB);

                    if (cntA <= 0) throw new InvalidOperationException("RESOLVE_A;Fail;reason=no items");
                    if (cntB <= 0) throw new InvalidOperationException("RESOLVE_B;Fail;reason=no items");

                    var clashDoc = Application.MainDocument.GetClash();
                    var td = clashDoc.TestsData;

                    var test = new Autodesk.Navisworks.Api.Clash.ClashTest
                    {
                        DisplayName = dto.test_name + " " + DateTime.UtcNow.ToString("HHmmssfff"),
                        TestType = Autodesk.Navisworks.Api.Clash.ClashTestType.Hard,
                        Tolerance = args.tolerance_m
                    };
                    test.SelectionA.Selection.CopyFrom(selA);
                    test.SelectionB.Selection.CopyFrom(selB);

                    td.TestsAddCopy(test);

                    var added = td.Tests.OfType<ClashTest>()
                        .FirstOrDefault(t => t.DisplayName == test.DisplayName);

                    if (added == null) throw new InvalidOperationException("test not found after AddCopy");

                    td.TestsRunTest(added);

                    ClashTest fresh = null;
                    for (int i = 0; i < 10; i++)
                    {
                        fresh = td.Tests.OfType<ClashTest>()
                            .FirstOrDefault(t => t.DisplayName == test.DisplayName);
                        if (fresh != null && fresh.Children != null) break;
                        System.Threading.Thread.Sleep(20);
                    }
                    if (fresh == null) throw new InvalidOperationException("test not found after Run");

                    dto.results = CountClashResultsSafe(added);
                    dto.message = $"ok; results={dto.results}";

                    try
                    {
                        string MakeJson(List<ScopeAppliedInfo> lst)
                        {
                            if (lst == null) return "[]";
                            var sb = new StringBuilder();
                            sb.Append('[');
                            for (int i = 0; i < lst.Count; i++)
                            {
                                var x = lst[i];
                                sb.Append("{")
                                  .AppendFormat("\"input_id\":\"{0}\",", EscapeJson(x.input_id ?? ""))
                                  .AppendFormat("\"resolved_id\":\"{0}\",", EscapeJson(x.resolved_id ?? ""))
                                  .AppendFormat("\"applied_id\":\"{0}\",", EscapeJson(x.applied_id ?? ""))
                                  .AppendFormat("\"reason\":\"{0}\",", EscapeJson(x.reason ?? ""))
                                  .AppendFormat("\"element_name\":\"{0}\"", EscapeJson(x.element_name ?? ""))
                                  .Append("}");
                                if (i < lst.Count - 1) sb.Append(",");
                            }
                            sb.Append(']');
                            return sb.ToString();
                        }

                        dto.details =
                            "{"
                            + "\"scopeA_info\":" + MakeJson(infosA) + ","
                            + "\"scopeB_info\":" + MakeJson(infosB)
                            + "}";
                    }
                    catch { }

                    return dto;
                });
            }
            catch (Exception ex)
            {
                dto.success = false;
                dto.message = ex.Message;
                LogHelper.LogError($"[CLASH][NET] FAILED: {ex}");
            }

            LogHelper.LogEvent("[CLASH][NET] RunClashAsync EXIT");
            return dto;
        }

        private Autodesk.Navisworks.Api.Selection ResolveScopeSelection(
            Autodesk.Navisworks.Api.Document doc,
            string scope,
            out int itemCount,
            out bool expanded)
        {
            var col = new Autodesk.Navisworks.Api.ModelItemCollection();
            expanded = false;
            itemCount = 0;

            if (doc == null || doc.Models == null)
                return new Autodesk.Navisworks.Api.Selection(col);

            // FIX: Handle "all" or empty scope → expand entire document
            var trimmedScope = (scope ?? "").Trim();
            if (string.IsNullOrEmpty(trimmedScope) ||
                trimmedScope.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var root in doc.Models.RootItems)
                    col.AddRange(root.DescendantsAndSelf);
                expanded = true;
                itemCount = col.Count;
                return new Autodesk.Navisworks.Api.Selection(col);
            }

            var tokens = (scope ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(t => t.Trim())
                                      .Where(t => !string.IsNullOrWhiteSpace(t))
                                      .ToList();

            if (tokens.Count > 0 && tokens.Any(LooksLikeCanonicalId))
            {
                List<ScopeAppliedInfo> infosA;
                var sel = BuildSelectionFromCanonicalTokens(doc, tokens, out itemCount, out infosA);
                expanded = false;
                try
                {
                    var appliedList = string.Join("; ", infosA.Select(i => $"{i.input_id} -> {i.applied_id} [{i.reason}]"));
                    LogHelper.LogInfo($"[CLASH][SCOPE] Canonical promotion: {appliedList}");
                }
                catch { }
                return sel;
            }

            var rootSet = new HashSet<Autodesk.Navisworks.Api.ModelItem>();
            foreach (var token in tokens)
            {
                var items = ResolveItemsByCanonicalIds(doc, new[] { token });
                if (items != null && items.Count > 0)
                {
                    foreach (var it in items)
                    {
                        var root = GetModelRootOf(it);
                        if (root != null && rootSet.Add(root))
                            col.AddRange(root.DescendantsAndSelf);
                    }
                }
            }
            if (col.Count > 0)
            {
                expanded = true;
                itemCount = col.Count;
                return new Autodesk.Navisworks.Api.Selection(col);
            }

            foreach (var token in tokens)
            {
                string diag;
                var roots = ResolveScopeToModelRoots(doc, token, CancellationToken.None, out diag) ?? new List<Autodesk.Navisworks.Api.ModelItem>();
                foreach (var r in roots)
                    if (r != null && rootSet.Add(r))
                        col.AddRange(r.DescendantsAndSelf);
            }
            if (col.Count > 0)
            {
                expanded = true;
                itemCount = col.Count;
                return new Autodesk.Navisworks.Api.Selection(col);
            }

            foreach (var root in doc.Models.RootItems)
            {
                var name = root.DisplayName ?? "";
                if (tokens.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    col.AddRange(root.DescendantsAndSelf);
            }

            expanded = col.Count > 0;
            itemCount = col.Count;
            return new Autodesk.Navisworks.Api.Selection(col);
        }

        private Selection BuildSelectionWithPromotionIfCanonical(
            Document doc, string scope,
            out int itemCount, out bool expanded,
            out List<ScopeAppliedInfo> infos)
        {
            itemCount = 0; expanded = false; infos = new List<ScopeAppliedInfo>();

            var tokens = (scope ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(t => t.Trim())
                                      .Where(t => !string.IsNullOrWhiteSpace(t))
                                      .ToList();

            if (tokens.Count > 0 && tokens.Any(LooksLikeCanonicalId))
            {
                return BuildSelectionFromCanonicalTokens(doc, tokens, out itemCount, out infos);
            }

            return ResolveScopeSelection(doc, scope, out itemCount, out expanded);
        }

        public Task<PropertyItemListDto> ListItemsToPropertyAsync(
            ListItemsToPropertyArgs args,
            CancellationToken ct)
        {
            LogHelper.LogEvent($"list_items_to_property: cat='{args.category}', prop='{args.property}', scope='{args.Scope}', modelFilter='{args.ModelFilter}', valueFilter='{args.ValueFilter}', ignoreCase={args.IgnoreCase}", "ListItemsToPropertyAsync");

            return UiThread.InvokeAsync(() =>
            {
                var result = new PropertyItemListDto
                {
                    category = args.category,
                    property = args.property,
                    Scope = args.Scope,
                    ModelFilter = args.ModelFilter,
                    ValueFilter = args.ValueFilter,
                    IgnoreCase = args.IgnoreCase,
                    Items = new List<PropertyItemDto>()
                };

                var doc = Application.ActiveDocument;
                if (doc == null)
                {
                    LogHelper.LogWarning("Kein aktives Dokument.", "ListItemsToPropertyAsync");
                    return result;
                }

                var candidates = ResolveScopeItems(doc, args.Scope);
                LogHelper.LogDebug($"Scope liefert {candidates.Count} Kandidaten.", "ListItemsToPropertyAsync");

                if (!string.IsNullOrWhiteSpace(args.ModelFilter))
                {
                    var allowedRoots = GetAllowedModelRootsByModelFilter(doc, args.ModelFilter, ct);
                    if (allowedRoots.Count == 0)
                    {
                        LogHelper.LogInfo($"ModelFilter '{args.ModelFilter}' ergab keine Treffer (keine passenden Submodel-Roots).");
                        candidates = new List<ModelItem>();
                    }
                    else
                    {
                        candidates = candidates
                            .Where(mi => {
                                var r = GetModelRootOf(mi);
                                return r != null && allowedRoots.Contains(r);
                            })
                            .ToList();
                        LogHelper.LogDebug($"Nach ModelFilter '{args.ModelFilter}' verbleiben {candidates.Count} Items (Root-basiert).", "ListItemsToPropertyAsync");
                    }
                }

                var comparer = BuildValuePredicate(args.ValueFilter, args.IgnoreCase);
                int added = 0;
                int? max = args.MaxResults ?? 200; // default cap to avoid MCP_Launcher timeout

                foreach (var item in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    var hasValue = TryReadDisplayValue(item, args.category, args.property, out var value, out var rawVariant);
                    if (!hasValue) continue;

                    if (comparer == null || comparer(value))
                    {
                        var modelRoot = GetModelRootOf(item);
                        var modelCid = GetCanonicalId(modelRoot);
                        var ident = GetModelIdentityFromItem(modelRoot);
                        var modelName = ident.fileOnly ?? "";
                        var displayPath = GetPathSteps(item, includeCanonical: true, reverse: true);
                        var cid = GetCanonicalId(item);

                        result.Items.Add(new PropertyItemDto
                        {
                            canonical_id = cid,
                            path_from_this_object = displayPath,
                            model_name = modelName,
                            model_canonical_id = modelCid,
                            PropertyValue = value
                        });
                        added++;

                        if (max.HasValue && added >= max.Value) break;
                    }
                }

                result.count = result.Items.Count;
                LogHelper.LogEvent($"list_items_to_property: {result.count} Treffer.", "ListItemsToPropertyAsync");
                return result;
            });
        }

        private HashSet<ModelItem> GetAllowedModelRootsByModelFilter(Document doc, string modelFilter, CancellationToken ct)
        {
            var allowed = new HashSet<ModelItem>();
            if (string.IsNullOrWhiteSpace(modelFilter) || doc == null) return allowed;

            var tokens = modelFilter
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (tokens.Count == 0) return allowed;

            var subs = ScanSubModels(doc, ct, false);
            foreach (var sm in subs)
            {
                var cid = sm.CanonicalId ?? "";
                var fileOnly = sm.FileOnly ?? "";
                var display = sm.Display ?? "";
                var ext = sm.Ext ?? "";

                foreach (var tk in tokens)
                {
                    if (!string.IsNullOrEmpty(cid) && cid.Equals(tk, StringComparison.OrdinalIgnoreCase))
                    { allowed.Add(sm.Root); break; }
                    if (!string.IsNullOrEmpty(fileOnly) && fileOnly.Equals(tk, StringComparison.OrdinalIgnoreCase))
                    { allowed.Add(sm.Root); break; }
                    if (!string.IsNullOrEmpty(display) && display.Equals(tk, StringComparison.OrdinalIgnoreCase))
                    { allowed.Add(sm.Root); break; }
                    if (!string.IsNullOrEmpty(ext) && ext.Equals(tk, StringComparison.OrdinalIgnoreCase))
                    { allowed.Add(sm.Root); break; }
                }
            }

            return allowed;
        }

        private List<ModelItem> ResolveScopeItems(Document doc, string scope)
        {
            return doc?.Models?.RootItems?
                .SelectMany(root => root.DescendantsAndSelf)
                .ToList()
                ?? new List<ModelItem>();
        }

        private bool TryReadDisplayValue(ModelItem item, string category, string property, out string display, out Autodesk.Navisworks.Api.PropertyCategory rawVariantOwner)
        {
            display = null;
            rawVariantOwner = null;

            var cats = item?.PropertyCategories;
            if (cats == null) return false;

            foreach (PropertyCategory cat in cats)
            {
                if (cat == null || string.IsNullOrEmpty(cat.DisplayName)) continue;
                if (string.Equals(cat.DisplayName, category, StringComparison.OrdinalIgnoreCase))
                {
                    rawVariantOwner = cat;
                    foreach (DataProperty dp in cat.Properties)
                    {
                        if (dp == null || string.IsNullOrEmpty(dp.DisplayName)) continue;
                        if (string.Equals(dp.DisplayName, property, StringComparison.OrdinalIgnoreCase))
                        {
                            display = FormatVariantValue(dp.Value);
                            return !string.IsNullOrEmpty(display);
                        }
                    }
                }
            }
            return false;
        }

        private Func<string, bool> BuildValuePredicate(string valueFilter, bool ignoreCase)
        {
            if (string.IsNullOrWhiteSpace(valueFilter)) return null;

            if (valueFilter.Length >= 2 && valueFilter[0] == '/' && valueFilter.Last() == '/')
            {
                var pattern = valueFilter.Substring(1, valueFilter.Length - 2);
                var rx = new System.Text.RegularExpressions.Regex(pattern);
                return s => s != null && rx.IsMatch(s);
            }
            if (valueFilter.Length >= 3 && valueFilter.StartsWith("/") && valueFilter.EndsWith("/i"))
            {
                var pattern = valueFilter.Substring(1, valueFilter.Length - 3);
                var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return s => s != null && rx.IsMatch(s);
            }

            string[] ops = new[] { ">=", "<=", "==", "!=", ">", "<", "~=", "^=", "$=" };
            string op = null;
            string rhs = null;

            foreach (var candidate in ops)
            {
                if (valueFilter.StartsWith(candidate, StringComparison.Ordinal))
                {
                    op = candidate;
                    rhs = valueFilter.Substring(candidate.Length);
                    break;
                }
            }

            if (op == null)
            {
                var needle = ignoreCase ? valueFilter.ToLowerInvariant() : valueFilter;
                return s =>
                {
                    if (s == null) return false;
                    var hay = ignoreCase ? s.ToLowerInvariant() : s;
                    return hay.Contains(needle);
                };
            }

            rhs = (rhs ?? "").Trim();
            double rhsNum;
            bool rhsIsNum = double.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out rhsNum);

            switch (op)
            {
                case "==":
                    return s => string.Equals(Norm(s, ignoreCase), Norm(rhs, ignoreCase), ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                case "!=":
                    return s => !string.Equals(Norm(s, ignoreCase), Norm(rhs, ignoreCase), ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                case ">":
                case ">=":
                case "<":
                case "<=":
                    if (!rhsIsNum) return _ => false;
                    return s =>
                    {
                        if (!TryParseInvariant(Norm(s, ignoreCase), out var l)) return false;
                        switch (op)
                        {
                            case ">": return l > rhsNum;
                            case ">=": return l >= rhsNum;
                            case "<": return l < rhsNum;
                            case "<=": return l <= rhsNum;
                        }
                        return false;
                    };
                case "~=":
                    return s => Contains(Norm(s, ignoreCase), Norm(rhs, ignoreCase), ignoreCase);
                case "^=":
                    return s =>
                    {
                        var L = Norm(s, ignoreCase);
                        var R = Norm(rhs, ignoreCase);
                        return L != null && R != null && L.StartsWith(R, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    };
                case "$=":
                    return s =>
                    {
                        var L = Norm(s, ignoreCase);
                        var R = Norm(rhs, ignoreCase);
                        return L != null && R != null && L.EndsWith(R, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    };
            }
            return null;

            string Norm(string x, bool ic) => x?.Trim();
            bool Contains(string a, string b, bool ic)
            {
                if (a == null || b == null) return false;
                if (ic) { a = a.ToLowerInvariant(); b = b.ToLowerInvariant(); }
                return a.Contains(b);
            }
            bool TryParseInvariant(string s, out double val) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private ModelItem GetModelRootOf(ModelItem item)
        {
            if (item == null) return null;
            ModelItem root = null;
            foreach (var n in item.AncestorsAndSelf ?? Enumerable.Empty<ModelItem>())
                root = n;
            return root;
        }

        private (string fileOnly, string ext, string display) GetModelIdentityFromItem(ModelItem item)
        {
            var r = ResolveSubModelIdentity(item: item);
            var fileOnly = string.IsNullOrWhiteSpace(r.fileOnly) ? r.display : r.fileOnly;
            return (fileOnly, r.ext, r.display);
        }

        private Document RequireDocument()
        {
            try
            {
                var doc = Application.ActiveDocument;
                if (doc == null)
                    LogHelper.LogWarning("[FALLBACK] 🔍 Aktives Dokument: none");
                else
                    LogHelper.LogEvent($"[FALLBACK] 🔍 Aktives Dokument: Title='{doc.Title}', File='{doc.FileName}'");
                return doc;
            }
            catch (Exception ex)
            {
                LogHelper.LogError($"[FALLBACK] Fehler beim Zugriff auf ActiveDocument: {ex.Message}");
                return null;
            }
        }

        private IEnumerable<ModelItem> EnumerateAllItems(Document doc)
        {
            if (doc == null || doc.Models == null) yield break;
            foreach (Model m in doc.Models)
            {
                var root = m?.RootItem;
                if (root == null) continue;
                foreach (var mi in root.DescendantsAndSelf ?? Enumerable.Empty<ModelItem>())
                    yield return mi;
            }
        }

        private (string fileOnly, string ext, string display, string source)
        ResolveSubModelIdentity(Model model = null, ModelItem item = null)
        {
            try
            {
                if (model != null)
                {
                    var raw = !string.IsNullOrWhiteSpace(model?.SourceFileName) ? model.SourceFileName : model?.FileName;
                    var ext = string.IsNullOrWhiteSpace(raw) ? "" : Path.GetExtension(raw);
                    var fileOnly = string.IsNullOrWhiteSpace(raw) ? "" : Path.GetFileName(raw);
                    var display = string.IsNullOrWhiteSpace(fileOnly) ? "" : Path.GetFileNameWithoutExtension(fileOnly);
                    if (!string.IsNullOrWhiteSpace(ext))
                        return (fileOnly, ext, display, "Model.Source/File");

                    var dn = model?.RootItem?.DisplayName ?? DEFAULT_UNNAMED;
                    var ext2 = string.IsNullOrWhiteSpace(dn) ? "" : Path.GetExtension(dn);
                    if (!string.IsNullOrWhiteSpace(ext2))
                        return (Path.GetFileName(dn), ext2, Path.GetFileNameWithoutExtension(dn), "Model.RootItem.DisplayName");

                    return (dn, "", dn, "Model.Fallback(no-ext)");
                }

                if (item != null)
                {
                    string candidate =
                        TryGetPropertyValue(item, "Item", "Source File")
                        ?? TryGetPropertyValue(item, "Element", "Quelldatei")
                        ?? item.DisplayName
                        ?? "";

                    string ext = string.IsNullOrWhiteSpace(candidate) ? "" : Path.GetExtension(candidate);
                    string fileOnly = string.IsNullOrWhiteSpace(candidate) ? "" : Path.GetFileName(candidate);
                    string display = string.IsNullOrWhiteSpace(fileOnly)
                        ? (item.DisplayName ?? DEFAULT_UNNAMED)
                        : Path.GetFileNameWithoutExtension(fileOnly);

                    if (string.IsNullOrWhiteSpace(ext))
                    {
                        var ifcClass =
                            TryGetPropertyValue(item, "LcOpGeometryProperty", "IFC Class")
                            ?? TryGetPropertyValue(item, "IFC", "Class")
                            ?? TryGetPropertyValue(item, "IFC", "IFC Class");
                        if (!string.IsNullOrWhiteSpace(ifcClass))
                            ext = ".ifc";
                    }

                    return (fileOnly, ext ?? "", display, "Item.props/display");
                }
            }
            catch { }

            return ("", "", DEFAULT_UNNAMED, "none");
        }

        private IEnumerable<(string fileOnly, string ext, string display, ModelItem item)>
        EnumerateSubModels(Document doc, CancellationToken ct)
        {
            var results = new List<(string fileOnly, string ext, string display, ModelItem item)>();

            try
            {
                var models = doc.Models;
                int mc = (models != null) ? models.Count : 0;
                LogHelper.LogInfo($"[FALLBACK] 📦 Document.Models.Count = {mc}");

                Model firstModel = null;
                if (mc > 0)
                {
                    foreach (Model m in models) { firstModel = m; break; }
                }

                bool looksLikeContainer = false;
                if (mc == 1 && firstModel != null)
                {
                    var raw1 = !string.IsNullOrWhiteSpace(firstModel.SourceFileName) ? firstModel.SourceFileName : firstModel.FileName;
                    var ext1 = string.IsNullOrWhiteSpace(raw1) ? "" : Path.GetExtension(raw1);
                    looksLikeContainer = string.IsNullOrWhiteSpace(ext1) || IsContainerExt(ext1);
                    LogHelper.LogInfo($"[FALLBACK] 🔎 Single model ext='{ext1}', looksLikeContainer={looksLikeContainer}");
                }

                if (mc > 1 || (mc == 1 && !looksLikeContainer))
                {
                    LogHelper.LogInfo("[FALLBACK] Branch: NWF/unsaved → iteriere doc.Models.");
                    foreach (Model m in models)
                    {
                        if (ct.IsCancellationRequested) { LogHelper.LogInfo("[FALLBACK] ⏹️ Abgebrochen (CT)."); break; }

                        var resolved = ResolveSubModelIdentity(model: m);
                        string fileOnly = resolved.fileOnly;
                        string ext = resolved.ext;
                        string display = string.IsNullOrWhiteSpace(resolved.display) ? fileOnly : resolved.display;

                        display = SafeNameOrDefault(display, fileOnly, DEFAULT_UNNAMED);
                        LogHelper.LogInfo($"[FALLBACK] 🧭 Model-Resolve: fileOnly='{fileOnly}', ext='{ext}', src={resolved.source}");

                        if (IsContainerExt(ext))
                        {
                            results.Add((SafeNameOrDefault(fileOnly, display, DEFAULT_UNNAMED), ext, display, m.RootItem));
                            LogHelper.LogInfo($"[FALLBACK] ➕ Container aufgenommen: '{display}' ({ext})");

                            var children = m.RootItem?.Children;
                            int cc = (children != null) ? children.Count() : 0;
                            LogHelper.LogInfo($"[FALLBACK]   ↳ Children Count = {cc}");

                            if (children != null && cc > 0)
                            {
                                foreach (ModelItem child in children)
                                {
                                    if (ct.IsCancellationRequested) { LogHelper.LogInfo("[FALLBACK] ⏹️ Abgebrochen (CT)."); break; }
                                    var cres = ResolveSubModelIdentity(item: child);
                                    var cdisplay = SafeNameOrDefault(cres.display, cres.fileOnly, DEFAULT_UNNAMED);
                                    results.Add((SafeNameOrDefault(cres.fileOnly, cdisplay, DEFAULT_UNNAMED), cres.ext, cdisplay, child));
                                }
                            }
                            continue;
                        }

                        results.Add((SafeNameOrDefault(fileOnly, display, DEFAULT_UNNAMED), ext, display, m.RootItem));
                    }
                }
                else if (mc == 1 && looksLikeContainer)
                {
                    LogHelper.LogInfo("[FALLBACK] Branch: NWD/NWF → iteriere RootItem.Children (Top-Level-Modelle).");
                    var children = firstModel?.RootItem?.Children;
                    int childCount = (children != null) ? children.Count() : 0;
                    LogHelper.LogInfo($"[FALLBACK] RootItem.Children Count = {childCount}");

                    if (children != null && childCount > 0)
                    {
                        foreach (ModelItem child in children)
                        {
                            if (ct.IsCancellationRequested) { LogHelper.LogInfo("[FALLBACK] ⏹️ Abgebrochen (CT)."); break; }
                            var resolved = ResolveSubModelIdentity(item: child);
                            var display = SafeNameOrDefault(resolved.display, resolved.fileOnly, DEFAULT_UNNAMED);
                            results.Add((SafeNameOrDefault(resolved.fileOnly, display, DEFAULT_UNNAMED), resolved.ext, display, child));
                        }
                    }
                }
                else
                {
                    LogHelper.LogInfo("[FALLBACK] ℹ️ Keine Models vorhanden (doc.Models leer).");
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogWarning($"[FALLBACK] EnumerateSubModels: Fehler beim Erfassen der Untermodelle: {ex.Message}", "FALLBACK");
            }

            return results;
        }

        private List<SubModel> ScanSubModels(Document doc, CancellationToken ct, bool includeContainers)
        {
            var list = new List<SubModel>();
            foreach (var tup in EnumerateSubModels(doc, ct))
            {
                var isCont = IsContainerExt(tup.ext);
                if (!includeContainers && isCont) continue;

                var cid = GetCanonicalId(tup.item);
                list.Add(new SubModel
                {
                    FileOnly = SafeNameOrDefault(tup.fileOnly, tup.display, DEFAULT_UNNAMED),
                    Ext = tup.ext ?? "",
                    Display = SafeNameOrDefault(tup.display, tup.fileOnly, DEFAULT_UNNAMED),
                    Root = tup.item,
                    CanonicalId = cid,
                    IsContainer = isCont
                });
            }
            return list;
        }

        public Task<DtoList<ModelDetailDto>> ListModelsAsync(CancellationToken ct)
        {
            LogHelper.LogEvent("RPC list_models (Fallback) gestartet.");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();
                var list = new DtoList<ModelDetailDto>();
                if (doc == null)
                {
                    LogHelper.LogSuccess("[FALLBACK] list_models: 0 Modelle.");
                    return list;
                }

                bool isSaved;
                string containerFileNameOnly = GetContainerFileNameOnly(doc, out isSaved);
                LogHelper.LogInfo($"[FALLBACK] 🔍 Aktives Dokument: Title='{(string.IsNullOrWhiteSpace(doc.Title) ? "Unbenannt" : doc.Title)}', File='{(isSaved ? doc.FileName : "(nicht gespeichert)")}'");

                var subs = ScanSubModels(doc, ct, false);

                int subModelCount = 0;
                foreach (var sm in subs)
                {
                    if (string.IsNullOrWhiteSpace(sm.CanonicalId))
                        LogHelper.LogInfo("[FALLBACK] ℹ️ Keine CanonicalId verfügbar → Eintrag ist (noch) nicht selektierbar.");

                    list.Add(new ModelDetailDto
                    {
                        canonical_id = NullSafe(sm.CanonicalId),
                        FileName = NullSafe(sm.FileOnly),
                        SourceFileName = NullSafe(sm.Ext),
                        DisplayName = NullSafe(!string.IsNullOrWhiteSpace(sm.FileOnly) ? sm.FileOnly : sm.Display),
                        ChildrenCount = sm.Root?.Children?.Count() ?? 0,
                        DescendantsCount = sm.Root?.DescendantsAndSelf?.Count() ?? 0,
                        perent_canonical_id = GetCanonicalId(sm.Root?.Parent)
                    });
                    subModelCount++;
                }

                list.Insert(0, new ModelDetailDto
                {
                    canonical_id = Guid.NewGuid().ToString("D"),
                    DisplayName = !string.IsNullOrWhiteSpace(doc.Title)
                                ? doc.Title
                                : (isSaved ? Path.GetFileNameWithoutExtension(containerFileNameOnly) : "Unbenannt"),
                    FileName = containerFileNameOnly,
                    SourceFileName = Path.GetExtension(containerFileNameOnly),
                    ChildrenCount = subModelCount,
                    DescendantsCount = subModelCount
                });

                LogHelper.LogSuccess($"[FALLBACK] list_models: {list.Count} Eintrag(e) (inkl. {subModelCount} Untermodell(e)).");
                return list;
            });
        }

        public Task<ModelOverviewDto> GetModelOverviewAsync(CancellationToken ct)
        {
            LogHelper.LogEvent("RPC get_model_overview (Fallback) gestartet.");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();

                var dto = new ModelOverviewDto
                {
                    categories_histogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    available_categories = new List<string>(),
                    Models = new List<ModelDetailDto>(),
                    total_items = 0,
                    TotalElements = 0,
                    ModelsCount = 0,
                    DocumentTitle = doc?.Title ?? string.Empty
                };

                if (doc == null)
                {
                    LogHelper.LogWarning("[FALLBACK] get_model_overview: Kein aktives Dokument.", "FALLBACK");
                    return dto;
                }

                try
                {
                    var title = string.IsNullOrWhiteSpace(doc.Title) ? "Unbenannt" : doc.Title;
                    var file = string.IsNullOrWhiteSpace(doc.FileName) ? "(nicht gespeichert)" : doc.FileName;
                    LogHelper.LogInfo($"[FALLBACK] 🔍 Aktives Dokument: Title='{title}', File='{file}'");

                    var subs = ScanSubModels(doc, ct, false);

                    foreach (var sm in subs)
                    {
                        if (ct.IsCancellationRequested) { LogHelper.LogInfo("[FALLBACK] ⏹️ Abgebrochen (CT)."); break; }

                        if (string.IsNullOrWhiteSpace(sm.CanonicalId))
                            LogHelper.LogError("[FALLBACK] ℹ️ Keine CanonicalId verfügbar → Modell in Übersicht, aber nicht selektierbar.", "GetModelOverviewAsync");

                        dto.TotalElements += sm.Root?.Children?.Count() ?? 0;
                        dto.Models.Add(new ModelDetailDto
                        {
                            FileName = NullSafe(!string.IsNullOrWhiteSpace(sm.FileOnly) ? sm.FileOnly : sm.Display),
                            SourceFileName = NullSafe(sm.Ext),
                            DisplayName = NullSafe(sm.Display),
                            ChildrenCount = sm.Root?.Children?.Count() ?? 0,
                            DescendantsCount = sm.Root?.DescendantsAndSelf?.Count() ?? 0,
                            canonical_id = NullSafe(sm.CanonicalId),
                            perent_canonical_id = GetCanonicalId(sm.Root?.Parent)
                        });
                    }

                    dto.ModelsCount = dto.Models.Count;
                    LogHelper.LogInfo($"[FALLBACK] ✅ ModelsCount (aus dto.Models.Count) = {dto.ModelsCount}");
                    LogHelper.LogSuccess($"[FALLBACK] get_model_overview: {dto.ModelsCount} Untermodell(e) gelistet.");
                }
                catch (Exception ex)
                {
                    LogHelper.LogWarning($"[FALLBACK] get_model_overview: Fehler beim Erfassen der Untermodelle: {ex.Message}", "FALLBACK");
                }

                return dto;
            });
        }

        public Task<UnitInfoDto> GetUnitsAndTolerancesAsync(CancellationToken ct)
        {
            LogHelper.LogEvent("RPC get_units_and_tolerances (Fallback) gestartet.");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();

                var unitsStr = doc?.Units.ToString() ?? "";
                string lengthUnit =
                    unitsStr.IndexOf("feet", StringComparison.OrdinalIgnoreCase) >= 0 ? "ft" :
                    unitsStr.IndexOf("inch", StringComparison.OrdinalIgnoreCase) >= 0 ? "in" :
                    "mm";

                var dto = new UnitInfoDto
                {
                    length_unit = lengthUnit,
                    area_unit = "m2",
                    volume_unit = "m3",
                    length_tolerance = 0.001
                };

                LogHelper.LogSuccess($"[FALLBACK] get_units_and_tolerances: length='{dto.length_unit}', tol={dto.length_tolerance.ToString(CultureInfo.InvariantCulture)}");
                return dto;
            });
        }

        public Task<ElementCountDto> GetPropertyDistributionByCategoryAsync(CancellationToken ct)
        {
            const string TAG = "[FALLBACK] GetPropertyDistributionByCategoryAsync / ID13453 (Model→Category→Property + Overview)";
            LogHelper.LogEvent($"{TAG} gestartet (Hinweis: 'category' wird ignoriert).");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();
                var dto = new ElementCountDto
                {
                    category = "(all)",
                    scope = "all",
                    count = 0,
                    success = true,
                    message = ""
                };

                if (doc == null)
                {
                    dto.success = false;
                    dto.message = "no document is open";
                    return dto;
                }

                try
                {
                    var tuple = BuildCategoryPropertyMaps(doc, ct);
                    var map = tuple.perModel;
                    var total = tuple.total;

                    dto.count = total;
                    dto.details = RenderCategoryStatsAsJson(map);
                    dto.message = RenderCategoryStatsAsMarkdown(map);

                    LogHelper.LogSuccess($"{TAG}: Gesamt gezählte Property-Werte = {total}");
                    return dto;
                }
                catch (Exception ex)
                {
                    dto.success = false;
                    dto.message = $"error: {ex.Message}";
                    LogHelper.LogError($"{TAG}: Fehler: {ex}");
                    return dto;
                }
            });
        }

        public Task<ElementCountDto> GetElementCountByCategoryAsync(string category, string scope, CancellationToken ct)
        {
            LogHelper.LogEvent($"RPC get_element_count_by_category (Fallback) gestartet: category='{category}', scope='{scope}'.");

            return UiThread.InvokeAsync(() =>
            {
                var t0 = DateTime.UtcNow;
                var doc = RequireDocument();
                var dto = new ElementCountDto
                {
                    category = NullSafe(category),
                    count = 0,
                    scope = string.IsNullOrWhiteSpace(scope) ? "all" : scope,
                    success = true,
                    message = "no warnings"
                };

                if (doc == null)
                {
                    dto.success = false;
                    dto.message = "no document is open";
                    LogHelper.LogWarning("[FALLBACK] get_element_count_by_category: Kein aktives Dokument.");
                    return dto;
                }
                if (string.IsNullOrWhiteSpace(category))
                {
                    dto.success = false;
                    dto.message = "category is empty";
                    LogHelper.LogWarning("[FALLBACK] get_element_count_by_category: 'category' ist leer.");
                    return dto;
                }

                var scopeToken = (scope ?? "").Trim();
                var restrictToModels = !string.IsNullOrWhiteSpace(scopeToken) && !scopeToken.Equals("all", StringComparison.OrdinalIgnoreCase);
                List<ModelItem> scopedRoots = null;

                if (restrictToModels)
                {
                    try
                    {
                        string diag;
                        scopedRoots = ResolveScopeToModelRoots(doc, scopeToken, ct, out diag);

                        if (scopedRoots == null || scopedRoots.Count == 0)
                        {
                            dto.success = false;
                            dto.message = $"scope(model) not matched: '{scopeToken}'. {diag}";
                            LogHelper.LogWarning($"[FALLBACK] get_element_count_by_category: scope '{scopeToken}' ergab keine Modelltreffer. {diag}");
                            return dto;
                        }

                        var picked = string.Join(", ", scopedRoots.Select(r =>
                        {
                            var cid = GetCanonicalId(r);
                            var disp = SafeNameOrDefault(r?.DisplayName, r?.ClassDisplayName, DEFAULT_UNNAMED);
                            return $"{disp}|cid:{cid}";
                        }));
                        LogHelper.LogInfo($"[FALLBACK] get_element_count_by_category: Scope aktiv. {scopedRoots.Count} Modell-Root(s) selektiert → [{picked}]");
                    }
                    catch (Exception ex)
                    {
                        dto.success = false;
                        dto.message = $"scope matching error: {ex.Message}";
                        LogHelper.LogError($"[FALLBACK] get_element_count_by_category: Fehler bei Scope-Zuordnung: {ex}");
                        return dto;
                    }
                }
                else
                {
                    LogHelper.LogDebug("[FALLBACK] get_element_count_by_category: Scope='all' → gesamtes Dokument wird gezählt.", "GetElementCountByCategoryAsync");
                }

                IEnumerable<ModelItem> sourceItems = restrictToModels
                    ? scopedRoots.SelectMany(r => r?.DescendantsAndSelf ?? Enumerable.Empty<ModelItem>())
                    : EnumerateAllItems(doc);

                int visited;
                int count;
                try
                {
                    var tuple = CountByCategory(sourceItems, category, ct);
                    count = tuple.count;
                    visited = tuple.visited;
                }
                catch (Exception ex)
                {
                    dto.success = false;
                    dto.message = $"counting error: {ex.Message}";
                    LogHelper.LogError($"[FALLBACK] get_element_count_by_category: Fehler während der Zählung: {ex}");
                    return dto;
                }

                dto.count = count;

                var tookMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (restrictToModels && count == 0)
                {
                    var modelIds = string.Join(", ", scopedRoots.Select(r => GetCanonicalId(r)));
                    dto.message = $"category '{category}' not found within scope(model): '{scopeToken}'. models=[{modelIds}]";
                    LogHelper.LogInfo($"[FALLBACK] get_element_count_by_category: Kategorie '{category}' im Scope '{scopeToken}' nicht gefunden. Visited={visited}, Took={tookMs}ms");
                }
                else
                {
                    LogHelper.LogSuccess($"[FALLBACK] get_element_count_by_category: scope='{(restrictToModels ? scopeToken : "all")}', category='{category}' → count={count}, visited={visited}, Took={tookMs}ms");
                }

                if (dto.count == 0)
                    dto.message += $" (category '{category}' not found, if scope was active than maybe help to search over scope=all)";

                return dto;
            });
        }

        public Task<List<SimpleItemRef>> ApplySelectionAsync(List<string> canonical_id, bool keepExistingSelection, CancellationToken ct)
        {
            LogHelper.LogEvent($"RPC apply_selection (Fallback) gestartet: ids={canonical_id?.Count ?? 0}, keepExisting={keepExistingSelection}");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();
                var resultList = new List<SimpleItemRef>();
                var notSelectable = new List<SimpleItemRef>();

                if (doc == null) return resultList;

                try
                {
                    if (ct.IsCancellationRequested) return resultList;

                    if (canonical_id == null || canonical_id.Count == 0)
                    {
                        LogHelper.LogWarning("[FALLBACK] apply_selection: Keine IDs übergeben. (Hinweis: Auswahl wird NICHT geleert – dafür clear_selection benutzen.)");
                        return resultList;
                    }

                    var items = ResolveItemsByCanonicalIds(doc, canonical_id);
                    if (items == null || items.Count == 0)
                    {
                        LogHelper.LogWarning("[FALLBACK] apply_selection: Keine der IDs auflösbar/selektierbar.");
                        return resultList;
                    }

                    ApplySelectionInternal(doc, items, keepExistingSelection);

                    foreach (var mi in items)
                    {
                        var info = Get_Simple_Item_Info(mi);
                        if (info != null) resultList.Add(info);
                    }

                    if (notSelectable.Count > 0)
                        BuildStringListSimpleItem(notSelectable);

                    LogHelper.LogSuccess($"[FALLBACK] apply_selection: matched={resultList.Count}, notSelectable={notSelectable.Count}");
                    return resultList;
                }
                catch (Exception ex)
                {
                    LogHelper.LogError($"[FALLBACK] apply_selection: Unerwarteter Fehler: {ex.Message}");
                    return resultList;
                }
            });
        }

        public Task<int> ClearSelectionAsync(CancellationToken ct)
        {
            LogHelper.LogEvent("RPC clear_selection (Fallback) gestartet.");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();
                int affected = 0;
                try
                {
                    affected = doc?.CurrentSelection?.SelectedItems?.Count ?? 0;

                    if (doc?.CurrentSelection != null)
                    {
                        doc.CurrentSelection.Clear();
                        LogHelper.LogSuccess($"[FALLBACK] clear_selection: cleared={affected}");
                    }
                    else
                    {
                        LogHelper.LogWarning("[FALLBACK] clear_selection: Keine aktuelle Selektion gefunden.");
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.LogError($"[FALLBACK] clear_selection: Fehler: {ex.Message}");
                }

                return affected;
            });
        }

        public Task<SelectionSnapshotDto> GetCurrentSelectionSnapshotAsync(CancellationToken ct)
        {
            LogHelper.LogEvent("RPC get_current_selection_snapshot (Fallback) gestartet.");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();
                var dto = new SelectionSnapshotDto
                {
                    count = 0,
                    canonical_id = new List<string>(),
                    path = new List<string>()
                };

                if (doc?.CurrentSelection?.SelectedItems == null)
                    return dto;

                var i = 0;
                foreach (ModelItem mi in doc.CurrentSelection.SelectedItems)
                {
                    dto.canonical_id.Add(GetCanonicalId(mi));
                    var steps = GetPathSteps(mi, includeCanonical: false, reverse: true);
                    dto.path.Add(steps.LastOrDefault()?.paths ?? "");
                    i++;
                }
                dto.count = i;

                LogHelper.LogSuccess($"[FALLBACK] get_current_selection_snapshot: count={dto.count}");
                return dto;
            });
        }

        public Task<ItemPropertiesDto> Get_ListProperties_For_Item(string itemId, CancellationToken ct)
        {
            const string M = "[FALLBACK] list_properties_for_item";
            LogHelper.LogEvent($"{M} gestartet: requestId='{itemId}'");

            return UiThread.InvokeAsync(() =>
            {
                var doc = RequireDocument();
                if (doc == null || doc.Models == null)
                {
                    LogHelper.LogWarning($"{M}: Kein aktives Dokument.");
                    return new ItemPropertiesDto { message = "", details = "No active document." };
                }

                var dto = new ItemPropertiesDto
                {
                    canonical_id = itemId ?? string.Empty,
                    element_name = string.Empty,
                    categories = new Dictionary<string, List<SimplePropJson>>(StringComparer.OrdinalIgnoreCase),
                    geometries = new Dictionary<string, List<SimplePropJson>>(StringComparer.OrdinalIgnoreCase),
                    child_from_this_object = new List<SimpleItemRef>(),
                    path_from_this_object = new List<PathStep>()
                };

                try
                {
                    var itemList = ResolveItemsByCanonicalIds(doc, new List<string> { itemId });
                    var item = (itemList == null || itemList.Count == 0) ? null : itemList.First();

                    if (item == null)
                    {
                        LogHelper.LogWarning($"{M}: Item '{itemId}' nicht gefunden.");
                        return dto;
                    }

                    string cid, name, typ, ifcGuid;
                    FillItemHead(item, out cid, out name, out typ, out ifcGuid);

                    dto.canonical_id = cid;
                    dto.element_name = name;
                    dto.typ = typ;
                    dto.interner_typ = item.ClassName ?? "";
                    dto.ifc_guid = ifcGuid;

                    var categories = Get_Property_Categories_To_Item(item);
                    var geometries = Get_Geometries_To_Item(item);
                    dto.geometries = geometries;
                    dto.categories = categories;

                    dto.child_from_this_object = Get_NachfolgeRecursive_From_Item(item, 1);
                    dto.path_from_this_object = GetPathSteps(item, includeCanonical: true, reverse: true);

                    LogHelper.LogSuccess($"{M}: OK für '{dto.canonical_id}'");
                    return dto;
                }
                catch (Exception ex)
                {
                    LogHelper.LogError($"{M}: Fehler: {ex.Message}");
                    return dto;
                }
            });
        }

        private (Dictionary<string, Dictionary<string, Dictionary<string, int>>> perModel, int total)
        BuildCategoryPropertyMaps(Document doc, CancellationToken ct)
        {
            var perModel = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>(StringComparer.OrdinalIgnoreCase);
            int totalCount = 0;

            foreach (var sm in ScanSubModels(doc, ct, false))
            {
                if (ct.IsCancellationRequested) break;
                var root = sm.Root;
                if (root == null) continue;

                string modelId = sm.CanonicalId;
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    var display = SafeNameOrDefault(sm.Display, sm.FileOnly, DEFAULT_UNNAMED);
                    modelId = "p:" + (display ?? "").GetHashCode().ToString("x8");
                }

                Dictionary<string, Dictionary<string, int>> catMap;
                if (!perModel.TryGetValue(modelId, out catMap))
                {
                    catMap = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                    perModel[modelId] = catMap;
                }

                foreach (var item in root.DescendantsAndSelf ?? Enumerable.Empty<ModelItem>())
                {
                    foreach (PropertyCategory cat in item?.PropertyCategories ?? Enumerable.Empty<PropertyCategory>())
                    {
                        var catName = SafeNameOrDefault(cat?.DisplayName, cat?.Name, DEFAULT_CATEGORY);

                        Dictionary<string, int> propMap;
                        if (!catMap.TryGetValue(catName, out propMap))
                        {
                            propMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            catMap[catName] = propMap;
                        }

                        foreach (DataProperty p in cat?.Properties ?? Enumerable.Empty<DataProperty>())
                        {
                            var propName = SafeNameOrDefault(p?.DisplayName, p?.Name, DEFAULT_PROPERTY);
                            var val = FormatVal(p);
                            if (string.IsNullOrWhiteSpace(val)) continue;

                            if (!propMap.ContainsKey(propName)) propMap[propName] = 0;
                            propMap[propName]++;
                            totalCount++;
                        }
                    }
                }
            }

            return (perModel, totalCount);
        }

        private (int count, int visited)
        CountByCategory(IEnumerable<ModelItem> sourceItems, string category, CancellationToken ct)
        {
            int count = 0, visited = 0;
            foreach (var item in sourceItems)
            {
                if (ct.IsCancellationRequested) break;
                if (item == null) continue;
                visited++;

                var cls = SafeNameOrDefault(item?.ClassDisplayName, item?.ClassName, "");
                if (!string.IsNullOrEmpty(cls) &&
                    cls.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                    continue;
                }

                foreach (PropertyCategory cat in item.PropertyCategories ?? Enumerable.Empty<PropertyCategory>())
                {
                    var catName = SafeNameOrDefault(cat?.DisplayName, cat?.Name, "");
                    if (!string.IsNullOrEmpty(catName) &&
                        catName.IndexOf(category, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        count++;
                        break;
                    }
                }
            }
            return (count, visited);
        }

        private bool LooksLikeCanonicalId(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            Guid g; if (Guid.TryParse(token, out g)) return true;
            return token.StartsWith("p:", StringComparison.OrdinalIgnoreCase) && token.Length > 2;
        }

        private string GetCanonicalId(ModelItem item)
        {
            if (item == null) return string.Empty;
            try
            {
                var g = item.InstanceGuid;
                if (g != Guid.Empty)
                    return g.ToString("D");
                return "p:" + ComputePathHash(item);
            }
            catch (Exception ex)
            {
                return $"ERR:{ex.Message}";
            }
        }

        private List<ModelItem> ResolveItemsByCanonicalIds(Document doc, IEnumerable<string> ids)
        {
            var result = new List<ModelItem>();
            try
            {
                var incoming = ids?.ToList() ?? new List<string>();
                var totalIn = incoming.Count;
                LogHelper.LogEvent($"Lookup: ResolveItemsByCanonicalIds gestartet. incomingIds={totalIn}");

                if (doc == null) { LogHelper.LogWarning("Lookup: Abbruch – kein aktives Dokument."); return result; }
                if (totalIn == 0) { LogHelper.LogWarning("Lookup: Abbruch – leere oder fehlende ID-Liste."); return result; }

                var sw = Stopwatch.StartNew();
                var idsInOrder = incoming.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                if (idsInOrder.Count == 0) { LogHelper.LogWarning("Lookup: Abbruch – alle übergebenen IDs sind leer/whitespace."); return result; }

                var resolvedById = new Dictionary<string, List<ModelItem>>(StringComparer.OrdinalIgnoreCase);
                var guidMap = new Dictionary<Guid, List<string>>();
                var hashMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var unknownIds = new List<string>();

                foreach (var id in idsInOrder)
                {
                    resolvedById[id] = new List<ModelItem>();
                    Guid g;
                    if (Guid.TryParse(id, out g))
                    {
                        List<string> lst;
                        if (!guidMap.TryGetValue(g, out lst)) { lst = new List<string>(); guidMap[g] = lst; }
                        lst.Add(id);
                        continue;
                    }
                    if (id.StartsWith("p:", StringComparison.OrdinalIgnoreCase) && id.Length > 2)
                    {
                        var h = id.Substring(2);
                        if (!string.IsNullOrWhiteSpace(h))
                        {
                            List<string> lst;
                            if (!hashMap.TryGetValue(h, out lst)) { lst = new List<string>(); hashMap[h] = lst; }
                            lst.Add(id);
                        }
                        else unknownIds.Add(id);
                        continue;
                    }
                    unknownIds.Add(id);
                }

                if (unknownIds.Count > 0)
                    LogHelper.LogWarning("Lookup: Unbekannte ID-Formate werden ignoriert:\n" + string.Join(", ", unknownIds.Take(20)));

                IEnumerable<ModelItem> allItems =
                    (doc.Models ?? Enumerable.Empty<Model>())
                    .Where(m => m != null && m.RootItem != null)
                    .SelectMany(m => m.RootItem.DescendantsAndSelf);

                int scanned = 0, matchedGuid = 0, matchedHash = 0;

                foreach (var mi in allItems)
                {
                    if (mi == null) { scanned++; continue; }

                    var ig = mi.InstanceGuid;
                    if (ig != Guid.Empty)
                    {
                        List<string> guidIds;
                        if (guidMap.TryGetValue(ig, out guidIds))
                        {
                            foreach (var id in guidIds) resolvedById[id].Add(mi);
                            matchedGuid++;
                        }
                    }

                    if (hashMap.Count > 0)
                    {
                        var calcHash = ComputePathHash(mi);
                        List<string> hashIds;
                        if (hashMap.TryGetValue(calcHash, out hashIds))
                        {
                            foreach (var id in hashIds) resolvedById[id].Add(mi);
                            matchedHash++;
                        }
                    }

                    scanned++;
                    if ((scanned % 50000) == 0)
                        LogHelper.LogDebug($"Lookup: Progress scanned={scanned}, matchedGuid={matchedGuid}, matchedHash={matchedHash}", "ResolveItemsByCanonicalIds");
                }

                var seen = new HashSet<ModelItem>();
                foreach (var id in idsInOrder)
                {
                    List<ModelItem> list;
                    if (resolvedById.TryGetValue(id, out list))
                        foreach (var mi in list)
                            if (mi != null && seen.Add(mi)) result.Add(mi);
                }

                sw.Stop();
                LogHelper.LogSuccess($"Lookup: Fertig. incoming={totalIn}, scanned={scanned}, uniqueResult={result.Count}, durationMs={sw.ElapsedMilliseconds}");

                if (result.Count == 0 && (guidMap.Count + hashMap.Count) > 0)
                    LogHelper.LogWarning("Lookup: Keine Treffer zu den übergebenen IDs gefunden.");

                return result;
            }
            catch (Exception ex)
            {
                LogHelper.LogError($"Lookup: Unerwarteter Fehler in ResolveItemsByCanonicalIds: {ex.Message}");
                return result;
            }
        }

        private void ApplySelectionInternal(Document doc, IEnumerable<ModelItem> items, bool keepExistingSelection)
        {
            var collection = new ModelItemCollection();
            foreach (var mi in items)
                if (mi != null) collection.Add(mi);

            if (collection.Count == 0) { LogHelper.LogWarning("[SEL/APPLY] Keine Items übergeben.", "MCP"); return; }

            if (!keepExistingSelection) doc.CurrentSelection.Clear();

            var state = ComApiBridge.State;
            if (state != null)
            {
                var comSel = ComApiBridge.ToInwOpSelection(collection);
                state.CurrentSelection = comSel;
            }
            else
            {
                var sel = new Selection(collection);
                if (keepExistingSelection) doc.CurrentSelection?.AddRange(collection);
                else doc.CurrentSelection?.CopyFrom(sel);
            }

            LogHelper.LogSuccess($"[SEL/APPLY] Selektiert={collection.Count}", "MCP");
        }

        private string TryGetPropertyValue(ModelItem item, string categoryDisplayOrApiName, string propertyDisplayOrApiName)
        {
            try
            {
                var cats = item?.PropertyCategories;
                if (cats == null) return null;

                foreach (PropertyCategory cat in cats)
                {
                    var cn = SafeNameOrDefault(cat?.DisplayName, cat?.Name, "");
                    if (!string.Equals(cn, categoryDisplayOrApiName, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (DataProperty p in cat?.Properties ?? Enumerable.Empty<DataProperty>())
                    {
                        var pn = SafeNameOrDefault(p?.DisplayName, p?.Name, "");
                        if (!string.Equals(pn, propertyDisplayOrApiName, StringComparison.OrdinalIgnoreCase)) continue;
                        try { return p?.Value?.ToDisplayString(); }
                        catch { return p?.Value?.ToString(); }
                    }
                }
            }
            catch (Exception ex) { LogHelper.LogDebug($"[FALLBACK] TryGetPropertyValue(): {ex.Message}"); }
            return null;
        }

        private string SafeNameOrDefault(string a, string b, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(a)) return a;
            if (!string.IsNullOrWhiteSpace(b)) return b;
            return fallback ?? "";
        }

        private string ComputePathHash(ModelItem item)
        {
            var parts = new List<string>();
            foreach (var a in item.AncestorsAndSelf)
                parts.Add(SafeNameOrDefault(a.DisplayName, a.ClassDisplayName ?? a.ClassName, DEFAULT_UNNAMED));
            var path = string.Join("/", parts);
            return path.GetHashCode().ToString("x8", CultureInfo.InvariantCulture);
        }

        private (string ifcClass, string ifcGuid) GetIfcMeta(ModelItem item)
        {
            string ifcClass =
                TryGetPropertyValue(item, "lcatfconsumer_Element_tab", "lcatfconsumer_parameter_IfcClass")
                ?? TryGetPropertyValue(item, "lcatfconsumer_Element_tab", "IfcClass")
                ?? TryGetPropertyValue(item, "lcatfconsumer_Type_tab", "IfcClass")
                ?? TryGetPropertyValue(item, "IFC", "Type")
                ?? TryGetPropertyValue(item, "IFC", "Class")
                ?? TryGetPropertyValue(item, "LcOaNode", "LcOaSceneBaseClassUserName")
                ?? item?.ClassDisplayName ?? item?.ClassName ?? "";

            string ifcGuid =
                TryGetPropertyValue(item, "LcATFIFCId", "IfcGUID")
                ?? TryGetPropertyValue(item, "LcATFIFCId", "IfcGlobalId")
                ?? TryGetPropertyValue(item, "Element-ID", "IfcGUID")
                ?? TryGetPropertyValue(item, "Element-ID", "GlobalId")
                ?? TryGetPropertyValue(item, "lcatfconsumer_Element_tab", "IfcGlobalId")
                ?? TryGetPropertyValue(item, "lcatfconsumer_Element_tab", "GlobalId")
                ?? TryGetPropertyValue(item, "lcatfconsumer_Element_tab", "IfcGUID")
                ?? "";

            return (ifcClass, ifcGuid);
        }

        private List<PathStep> GetPathSteps(ModelItem node, bool includeCanonical = true, bool reverse = true)
        {
            var result = new List<PathStep>();
            if (node == null) return result;

            var chain = (node.AncestorsAndSelf ?? Enumerable.Empty<ModelItem>()).ToList();
            if (reverse) chain.Reverse();

            var parts = new List<string>();
            foreach (var n in chain)
            {
                parts.Add(SafeNameOrDefault(n.DisplayName, n.ClassDisplayName ?? n.ClassName, DEFAULT_UNNAMED));
                var path = string.Join("/", parts);

                if (includeCanonical)
                    result.Add(new PathStep { canonical_id = NullSafe(GetCanonicalId(n)), paths = NullSafe(path) });
                else
                    result.Add(new PathStep { canonical_id = "", paths = NullSafe(path) });
            }
            return result;
        }

        private void CollectDescendantsRecursive(ModelItem node, List<ModelItem> acc, int currentDepth, int maxDepth)
        {
            if (node == null) return;
            if (currentDepth >= maxDepth) return;
            foreach (var child in node.Children ?? Enumerable.Empty<ModelItem>())
            {
                acc.Add(child);
                CollectDescendantsRecursive(child, acc, currentDepth + 1, maxDepth);
            }
        }

        private List<SimpleItemRef> Get_NachfolgeRecursive_From_Item(ModelItem root, int recursiceDeep)
        {
            var result = new List<SimpleItemRef>();
            if (root == null) return result;
            var acc = new List<ModelItem>();
            CollectDescendantsRecursive(root, acc, 0, recursiceDeep);
            foreach (var mi in acc)
            {
                var r = Get_Simple_Item_Info(mi);
                if (r != null) result.Add(r);
            }
            return result;
        }

        private SimpleItemRef Get_Simple_Item_Info(ModelItem it)
        {
            if (it == null) return null;
            var cid = GetCanonicalId(it);
            var name = SafeNameOrDefault(it.DisplayName, it.ClassDisplayName ?? it.ClassName, DEFAULT_UNNAMED);
            var meta = GetIfcMeta(it);
            var typ = !string.IsNullOrEmpty(meta.ifcClass) ? meta.ifcClass : (it.ClassDisplayName ?? it.ClassName ?? "");
            return new SimpleItemRef { canonical_id = NullSafe(cid), element_name = NullSafe(name), typ = NullSafe(typ) };
        }

        private Dictionary<string, List<SimplePropJson>> Get_Property_Categories_To_Item(ModelItem mi)
        {
            var map = new Dictionary<string, List<SimplePropJson>>(StringComparer.OrdinalIgnoreCase);

            Action<string, string, string, string> AddKV = (cat, key, type, val) =>
            {
                if (string.IsNullOrWhiteSpace(cat)) cat = DEFAULT_CATEGORY;
                if (string.IsNullOrWhiteSpace(key)) key = DEFAULT_PROPERTY;
                List<SimplePropJson> lst;
                if (!map.TryGetValue(cat, out lst)) { lst = new List<SimplePropJson>(); map[cat] = lst; }
                lst.Add(BuildSimplePropJson(key, type, val));
            };

            foreach (PropertyCategory cat in mi?.PropertyCategories ?? Enumerable.Empty<PropertyCategory>())
            {
                if (cat == null) continue;
                var catName = SafeNameOrDefault(cat.DisplayName, cat.Name, DEFAULT_CATEGORY);
                foreach (DataProperty p in cat.Properties ?? Enumerable.Empty<DataProperty>())
                {
                    if (p == null) continue;
                    var pName = SafeNameOrDefault(p.DisplayName, p.Name, DEFAULT_PROPERTY);
                    var pType = MapVariantType(p);
                    var pVal = FormatVal(p);
                    if (!string.IsNullOrWhiteSpace(pVal))
                        AddKV(catName, pName, pType, pVal);
                }
            }

            return map;
        }

        private (List<ModelItem> targets, string reason, int steps) ResolveGeometricTargets(ModelItem start)
        {
            var targets = new List<ModelItem>();
            int steps = 0;
            if (start == null) return (targets, "fallback:none", steps);

            if (Has_Geometry_From_Item(start)) { targets.Add(start); return (targets, "ok", steps); }

            foreach (var anc in start.Ancestors ?? Enumerable.Empty<ModelItem>())
            {
                steps++;
                if (Has_Geometry_From_Item(anc)) { targets.Add(anc); return (targets, "promoted:no-geometry", steps); }
            }

            var nearest = FindNearestGeometricDescendants(start);
            if (nearest.Count > 0) return (nearest, "demoted:nearest-descendants", steps);

            return (targets, "no-geometry-in-subtree", steps);
        }

        private List<ModelItem> FindNearestGeometricDescendants(ModelItem start, int maxDepth = 5, int maxNodes = 50000)
        {
            var result = new List<ModelItem>();
            if (start == null) return result;

            var q = new Queue<(ModelItem node, int depth)>();
            var seen = new HashSet<ModelItem>();
            q.Enqueue((start, 0)); seen.Add(start);

            while (q.Count > 0 && seen.Count <= maxNodes)
            {
                var (node, depth) = q.Dequeue();
                if (depth > maxDepth) break;

                var atThisLevel = new List<ModelItem>();
                foreach (var ch in node.Children ?? Enumerable.Empty<ModelItem>())
                {
                    if (ch == null || !seen.Add(ch)) continue;
                    if (Has_Geometry_From_Item(ch)) atThisLevel.Add(ch);
                    else q.Enqueue((ch, depth + 1));
                }

                if (atThisLevel.Count > 0) { result.AddRange(atThisLevel); break; }
            }
            return result;
        }

        private bool Has_Geometry_From_Item(ModelItem mi)
        {
            if (mi == null) return false;
            try
            {
                BoundingBox3D bb = null;
                try { bb = mi.BoundingBox(true); } catch { }
                if (bb == null) { try { bb = mi.BoundingBox(); } catch { } }
                if (bb == null) return false;
                double dx = Math.Max(0.0, bb.Max.X - bb.Min.X);
                double dy = Math.Max(0.0, bb.Max.Y - bb.Min.Y);
                double dz = Math.Max(0.0, bb.Max.Z - bb.Min.Z);
                return (dx > 0 || dy > 0 || dz > 0);
            }
            catch { return false; }
        }

        private Dictionary<string, List<SimplePropJson>> Get_Geometries_To_Item(ModelItem mi)
        {
            var geo = new Dictionary<string, List<SimplePropJson>>(StringComparer.OrdinalIgnoreCase);

            Action<string, string, string, string> AddObj = (cat, prop, type, value) =>
            {
                if (string.IsNullOrWhiteSpace(cat)) cat = "Geometry (Derived)";
                if (string.IsNullOrWhiteSpace(prop)) prop = DEFAULT_PROPERTY;
                List<SimplePropJson> lst;
                if (!geo.TryGetValue(cat, out lst)) { lst = new List<SimplePropJson>(); geo[cat] = lst; }
                lst.Add(BuildSimplePropJson(prop, type, value));
            };

            try
            {
                BoundingBox3D bb = null;
                try { bb = mi.BoundingBox(true); } catch { }
                if (bb == null) { try { bb = mi.BoundingBox(); } catch { } }

                if (bb != null && bb.Min != null && bb.Max != null)
                {
                    var dx = Math.Max(0.0, bb.Max.X - bb.Min.X);
                    var dy = Math.Max(0.0, bb.Max.Y - bb.Min.Y);
                    var dz = Math.Max(0.0, bb.Max.Z - bb.Min.Z);
                    var cx = (bb.Max.X + bb.Min.X) * 0.5;
                    var cy = (bb.Max.Y + bb.Min.Y) * 0.5;
                    var cz = (bb.Max.Z + bb.Min.Z) * 0.5;

                    const string CAT = "Geometry (Derived)";
                    AddObj(CAT, "Min", "point3d", "(" + FormulaJson(bb.Min.X) + ", " + FormulaJson(bb.Min.Y) + ", " + FormulaJson(bb.Min.Z) + ")");
                    AddObj(CAT, "Max", "point3d", "(" + FormulaJson(bb.Max.X) + ", " + FormulaJson(bb.Max.Y) + ", " + FormulaJson(bb.Max.Z) + ")");
                    AddObj(CAT, "SizeX", "double", FormulaJson(dx));
                    AddObj(CAT, "SizeY", "double", FormulaJson(dy));
                    AddObj(CAT, "SizeZ", "double", FormulaJson(dz));
                    AddObj(CAT, "Center", "point3d", "(" + FormulaJson(cx) + ", " + FormulaJson(cy) + ", " + FormulaJson(cz) + ")");
                }
            }
            catch { }

            return geo;
        }

        private string MapVariantType(DataProperty p)
        {
            try
            {
                var t = p?.Value?.DataType.ToString() ?? "unknown";
                if (t.Equals("Boolean", StringComparison.OrdinalIgnoreCase)) return "bool";
                if (t.Equals("DisplayString", StringComparison.OrdinalIgnoreCase)) return "string";
                if (t.Equals("IdentifierString", StringComparison.OrdinalIgnoreCase)) return "string";
                if (t.Equals("DateTime", StringComparison.OrdinalIgnoreCase)) return "datetime";
                if (t.Equals("Int32", StringComparison.OrdinalIgnoreCase)) return "int";
                if (t.Equals("Int64", StringComparison.OrdinalIgnoreCase)) return "long";
                if (t.Equals("NamedConstant", StringComparison.OrdinalIgnoreCase)) return "named";
                if (t.StartsWith("Double", StringComparison.OrdinalIgnoreCase)) return t.ToLowerInvariant();
                return t;
            }
            catch { return "unknown"; }
        }

        private string FormatVariant(VariantData v)
        {
            if (v == null) return "";
            try
            {
                switch (v.DataType)
                {
                    case VariantDataType.DisplayString:
                        var s = v.ToDisplayString() ?? "";
                        const string prefix = "DisplayString:";
                        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(prefix.Length);
                        return s;
                    case VariantDataType.DoubleLength: return v.ToDoubleLength().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.DoubleArea: return v.ToDoubleArea().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.DoubleVolume: return v.ToDoubleVolume().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.DoubleAngle: return v.ToDoubleAngle().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.Double: return v.ToDouble().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.Int32: return v.ToInt32().ToString(CultureInfo.CurrentCulture);
                    case VariantDataType.Boolean: return v.ToBoolean().ToString();
                    case VariantDataType.DateTime: return v.ToDateTime().ToString(CultureInfo.CurrentCulture);
                    default: return v.ToString();
                }
            }
            catch { return ""; }
        }

        public string FormatVariantValue(VariantData v) => FormatVariant(v);

        private string FormatVal(DataProperty p)
        {
            try
            {
                var v = p?.Value;
                if (v == null) return "";
                var s = FormatVariant(v);
                if (!string.IsNullOrWhiteSpace(s)) return s;
                try { s = v.ToDisplayString(); } catch { }
                if (!string.IsNullOrWhiteSpace(s)) return s;
                return v.ToString();
            }
            catch { return ""; }
        }

        private string FormulaJson(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private string EscapeJson(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        private SimplePropJson BuildSimplePropJson(string prop, string type, string value)
        {
            return new SimplePropJson { property = NullSafe(prop), type = NullSafe(type), value = NullSafe(value) };
        }

        private string GetContainerFileNameOnly(Document doc, out bool isSaved)
        {
            isSaved = false;
            try
            {
                var full = (string)doc.FileName;
                isSaved = !string.IsNullOrWhiteSpace(full);
                return isSaved ? Path.GetFileName(full) : "(nicht gespeichert)";
            }
            catch { return "(nicht gespeichert)"; }
        }

        private bool IsContainerExt(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            return ext.Equals(".nwd", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".nwf", StringComparison.OrdinalIgnoreCase);
        }

        public string BuildStringListSimpleItem(List<SimpleItemRef> list)
        {
            var sb = new StringBuilder();
            sb.Append("{ \"notSelectable\": [");
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                sb.Append("{");
                sb.AppendFormat("\"canonical_id\":\"{0}\",", it.canonical_id ?? "");
                sb.AppendFormat("\"element_name\":\"{0}\",", it.element_name ?? "");
                sb.AppendFormat("\"typ\":\"{0}\",", it.typ ?? "");
                sb.AppendFormat("\"details\":\"{0}\"", it.details ?? "");
                sb.Append("}");
                if (i < list.Count - 1) sb.Append(",");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string RenderCategoryStatsAsMarkdown(Dictionary<string, Dictionary<string, Dictionary<string, int>>> perModel)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📊 **Properties – Übersicht (pro Modell, Kategorie & Property)**");
            foreach (var modelKv in perModel)
            {
                sb.AppendLine();
                sb.AppendLine($"### Modell-ID: `{modelKv.Key}`");
                foreach (var catKv in modelKv.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"- **{catKv.Key}**");
                    foreach (var propKv in catKv.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        sb.AppendLine($"  - `{propKv.Key}`: {propKv.Value}");
                }
            }
            return sb.ToString();
        }

        private string RenderCategoryStatsAsJson(Dictionary<string, Dictionary<string, Dictionary<string, int>>> perModel)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool firstModel = true;
            foreach (var modelKv in perModel)
            {
                if (!firstModel) sb.Append(',');
                firstModel = false;
                sb.Append('\"').Append(EscapeJson(modelKv.Key)).Append("\":{");
                bool firstCat = true;
                foreach (var catKv in modelKv.Value)
                {
                    if (!firstCat) sb.Append(',');
                    firstCat = false;
                    sb.Append('\"').Append(EscapeJson(catKv.Key)).Append("\":{");
                    bool firstProp = true;
                    foreach (var propKv in catKv.Value)
                    {
                        if (!firstProp) sb.Append(',');
                        firstProp = false;
                        sb.Append('\"').Append(EscapeJson(propKv.Key)).Append("\":").Append(propKv.Value);
                    }
                    sb.Append('}');
                }
                sb.Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private List<ModelItem> ResolveScopeToModelRoots(Document doc, string scopeToken, CancellationToken ct, out string diagnostics)
        {
            diagnostics = "";
            var subs = ScanSubModels(doc, ct, true);
            var matches = new List<ModelItem>();

            foreach (var sm in subs)
            {
                if (ct.IsCancellationRequested) break;

                if (!string.IsNullOrWhiteSpace(sm.CanonicalId) && sm.CanonicalId.Equals(scopeToken, StringComparison.OrdinalIgnoreCase))
                { matches.Add(sm.Root); continue; }
                if (!string.IsNullOrWhiteSpace(sm.FileOnly) && sm.FileOnly.Equals(scopeToken, StringComparison.OrdinalIgnoreCase))
                { matches.Add(sm.Root); continue; }
                if (!string.IsNullOrWhiteSpace(sm.Display) && sm.Display.Equals(scopeToken, StringComparison.OrdinalIgnoreCase))
                { matches.Add(sm.Root); continue; }
                if (!string.IsNullOrWhiteSpace(sm.Ext) && sm.Ext.Equals(scopeToken, StringComparison.OrdinalIgnoreCase))
                { matches.Add(sm.Root); continue; }
            }

            if (matches.Count == 0)
            {
                var available = string.Join(", ", subs.Select(s =>
                    SafeNameOrDefault(s.FileOnly, s.Display, DEFAULT_UNNAMED) + "|" + s.Ext + "|" + s.CanonicalId));
                diagnostics = "available=[" + available + "]";
            }
            return matches;
        }

        private void FillItemHead(ModelItem item, out string cid, out string name, out string typ, out string ifcGuid)
        {
            cid = GetCanonicalId(item);
            name = SafeNameOrDefault(item.DisplayName, item.ClassDisplayName ?? item.ClassName, DEFAULT_UNNAMED);
            var meta = GetIfcMeta(item);
            var cls = !string.IsNullOrEmpty(meta.ifcClass) ? meta.ifcClass : (item.ClassDisplayName ?? item.ClassName ?? "");
            typ = cls ?? "";
            ifcGuid = meta.ifcGuid ?? "";
        }

        private static string NullSafe(string s) => s ?? "";
    }
}