using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class Program
{
    private static readonly HttpClient _http = new HttpClient
    {
        BaseAddress = new Uri("http://localhost:5050/")
    };

    static void Main(string[] args)
    {
        Console.Error.WriteLine("MCP Server started");

        string line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JObject root;
            try
            {
                root = JObject.Parse(line);
            }
            catch
            {
                continue;
            }

            string method = root["method"]?.ToString();
            JToken idToken = root["id"];

            if (idToken == null) continue;

            // Ã¢Å“â€¦ FIX: l'id JSON-RPC peut ÃƒÂªtre string ou int Ã¢â‚¬â€ on le garde tel quel
            JToken id = idToken;

            string response;

            switch (method)
            {
                case "initialize":
                    response = JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "navisworks-mcp", version = "1.0.0" }
                        }
                    });
                    break;

                case "tools/list":
                    response = JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            tools = BuildToolsList()
                        }
                    });
                    break;

                case "tools/call":
                    response = HandleToolCall(id, root["params"] as JObject);
                    break;

                default:
                    response = JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        error = new { code = -32601, message = "Method not found" }
                    });
                    break;
            }

            Console.WriteLine(response);
            Console.Out.Flush(); // IMPORTANT
        }
    }

    // Ã¢Å“â€¦ Liste des outils exposÃƒÂ©s ÃƒÂ  Claude Ã¢â‚¬â€ correspond aux routes rÃƒÂ©elles du serveur
    static object[] BuildToolsList()
    {
        return new object[]
        {
            new { name = "get_model_overview", description = "Get an overview of the loaded Navisworks model", inputSchema = EmptySchema() },
            new { name = "get_units_and_tolerances", description = "Get units and tolerances of the active document", inputSchema = EmptySchema() },
            new { name = "get_current_selection_snapshot", description = "Get the current selection in Navisworks", inputSchema = EmptySchema() },
            new { name = "clear_selection", description = "Clear the current selection in Navisworks", inputSchema = EmptySchema() },
            new { name = "get_element_count_by_category", description = "Count elements by category and scope", inputSchema = ObjectSchema(("category", "string"), ("scope", "string")) },
            new { name = "get_property_distribution_by_category", description = "Get property distribution statistics", inputSchema = ObjectSchema(("scope", "string")) },
            new { name = "list_items_to_property", description = "List items matching a property/value filter", inputSchema = ObjectSchema(("category", "string"), ("property", "string"), ("scope", "string"), ("max_results", "string")) },
            new { name = "run_simple_clash", description = "Run a simple clash detection between two scopes", inputSchema = ObjectSchema(("scopeA", "string"), ("scopeB", "string")) },
            new { name = "save_viewpoint", description = "Save the current viewpoint with a name", inputSchema = ObjectSchema(("name", "string")) },
            new { name = "list_viewpoints", description = "List all saved viewpoints in the document", inputSchema = EmptySchema() },
            new { name = "activate_viewpoint", description = "Activate a saved viewpoint by name", inputSchema = ObjectSchema(("name", "string")) },
            new { name = "hide_elements", description = "Hide elements by scope (canonical ID or model name)", inputSchema = ObjectSchema(("scope", "string")) },
            new { name = "show_elements", description = "Show (unhide) elements by scope (canonical ID or model name)", inputSchema = ObjectSchema(("scope", "string")) },
        };
    }

    static object EmptySchema()
    {
        return new { type = "object", properties = new { }, required = new string[] { } };
    }

    static object ObjectSchema(params (string name, string type)[] fields)
    {
        var props = new JObject();
        foreach (var f in fields)
            props[f.name] = new JObject { ["type"] = f.type };

        return new
        {
            type = "object",
            properties = props,
            required = new string[] { }
        };
    }

    // Ã¢Å“â€¦ FIX: relaie rÃƒÂ©ellement la requÃƒÂªte vers le serveur HTTP WAABE sur le port 5050
    static string HandleToolCall(JToken id, JObject toolParams)
    {
        try
        {
            string toolName = toolParams?["name"]?.ToString();
            JObject toolArgs = toolParams?["arguments"] as JObject ?? new JObject();

            if (string.IsNullOrWhiteSpace(toolName))
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    error = new { code = -32602, message = "Invalid params: missing tool name" }
                });
            }

            var rpcRequest = new
            {
                id = Guid.NewGuid().ToString("N"),
                method = toolName,
                @params = toolArgs
            };

            string rpcJson = JsonConvert.SerializeObject(rpcRequest);

            // Ã¢Å“â€¦ Appel HTTP synchrone vers le bridge Navisworks
            var rpcResponseJson = CallNavisworksRpc(rpcJson).GetAwaiter().GetResult();
            var rpcResult = JObject.Parse(rpcResponseJson);

            bool ok = rpcResult["ok"]?.Value<bool>() ?? false;

            if (!ok)
            {
                string errMsg = rpcResult["error"]?["msg"]?.ToString() ?? "Unknown error";
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new
                    {
                        content = new[]
                        {
                            new { type = "text", text = "Erreur Navisworks: " + errMsg }
                        },
                        isError = true
                    }
                });
            }

            string dataAsText = rpcResult["data"]?.ToString(Formatting.Indented) ?? "(no data)";

            return JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                id = id,
                result = new
                {
                    content = new[]
                    {
                        new { type = "text", text = dataAsText }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                id = id,
                result = new
                {
                    content = new[]
                    {
                        new { type = "text", text = "Exception cÃƒÂ´tÃƒÂ© MCP_Launcher: " + ex.Message }
                    },
                    isError = true
                }
            });
        }
    }

    static async Task<string> CallNavisworksRpc(string jsonBody)
    {
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("rpc", content);
        return await response.Content.ReadAsStringAsync();
    }
}
