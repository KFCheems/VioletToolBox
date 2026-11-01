using System.Text.Json;

namespace VioletToolBox.Agent;

/// <summary>
/// MCP (Model Context Protocol) server over stdio, JSON-RPC 2.0.
/// Lets any MCP-capable AI assistant (Claude Desktop, ZCode, …) use the toolbox
/// as native tools: initialize → tools/list → tools/call → JSON envelopes out.
/// </summary>
public sealed class McpServer
{
    private static readonly JsonSerializerOptions SlimJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextReader _in;
    private readonly TextWriter _out;
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "violettoolbox";
    private const string ServerVersion = "1.0";

    public McpServer(TextReader? input = null, TextWriter? output = null)
    {
        _in = input ?? Console.In;
        _out = output ?? Console.Error; // stdout carries ONLY JSON-RPC (MCP requirement)
    }

    /// <summary>Runs the request loop until stdin closes.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _out.WriteLine($"{ServerName}: MCP server on stdio — {CommandRegistry.Commands.Count} tools registered");
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await _in.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var response = await HandleAsync(line).ConfigureAwait(false);
            if (response is not null)
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, SlimJson)).ConfigureAwait(false);
        }
    }

    internal async Task<JsonElement?> HandleAsync(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl : (JsonElement?)null;
            if (!root.TryGetProperty("method", out var methodEl))
                return Error(id, -32600, "Invalid Request: missing method");

            switch (methodEl.GetString())
            {
                case "initialize":
                    return Result(id, new Dictionary<string, object?>
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?>() },
                        ["serverInfo"] = new Dictionary<string, object?>
                        {
                            ["name"] = ServerName, ["version"] = ServerVersion,
                        },
                    });

                case "notifications/initialized":
                case "initialized":
                    return null; // notification — no response

                case "tools/list":
                    return Result(id, new Dictionary<string, object?>
                    {
                        ["tools"] = CommandRegistry.Commands.Select(c => c.ToolSchema).ToList(),
                    });

                case "tools/call":
                {
                    if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var nameEl))
                        return Error(id, -32602, "Invalid params: tools/call requires name");
                    var toolName = nameEl.GetString() ?? "";

                    var command = CommandRegistry.Commands.FirstOrDefault(c => c.ToolName == toolName);
                    if (command is null)
                        return Error(id, -32602, $"Unknown tool: {toolName}");

                    var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (p.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in argsEl.EnumerateObject())
                        {
                            // map camelCase back to the registry's param names
                            var param = command.Params.FirstOrDefault(pa =>
                                AgentCommand.Camel(pa.Name).Equals(prop.Name, StringComparison.OrdinalIgnoreCase));
                            args[param?.Name ?? prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => prop.Value.GetRawText(),
                            };
                        }
                    }

                    // Destructive tools require the MCP-standard consent flag.
                    if (command.Destructive && !args.ContainsKey("__consent"))
                        args["__consent"] = p.TryGetProperty("_meta", out var meta)
                                         && meta.TryGetProperty("consent", out var consent)
                                         && consent.ValueKind == JsonValueKind.True;

                    var result = await CommandRegistry.RunAsync(command.Verb, args).ConfigureAwait(false);
                    // MCP tool result: content blocks with the JSON envelope as text.
                    return Result(id, new Dictionary<string, object?>
                    {
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = CommandRegistry.Serialize(result),
                            },
                        },
                        ["isError"] = !result.Ok,
                    });
                }

                case "ping":
                    return Result(id, new Dictionary<string, object?>());

                default:
                    return Error(id, -32601, $"Method not found: {methodEl.GetString()}");
            }
        }
    }

    private static JsonElement? Result(JsonElement? id, object payload) => Json(Object(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.GetRawText() is string raw ? JsonDocument.Parse(raw).RootElement : (object?)null!,
        ["result"] = payload,
    }));

    private static JsonElement? Error(JsonElement? id, int code, string message) => Json(Object(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.GetRawText() is string raw ? JsonDocument.Parse(raw).RootElement : (object?)null!,
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
    }));

    private static JsonElement Object(object o)
        => JsonSerializer.SerializeToElement(o, SlimJson);

    private static JsonElement? Json(JsonElement element) => element;
}
