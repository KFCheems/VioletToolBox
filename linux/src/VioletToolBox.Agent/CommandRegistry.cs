using System.Text.Json;
using System.Text.Json.Serialization;

namespace VioletToolBox.Agent;

/// <summary>Structured result envelope for every agent command — machine-readable first.</summary>
public sealed record AgentResult(
    bool Ok,
    [property: JsonPropertyOrder(1)] string Command,
    [property: JsonPropertyOrder(2)] object? Data,
    [property: JsonPropertyOrder(3)] string? Error,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> Log)
{
    public static AgentResult Success(string command, object? data, IReadOnlyList<string> log) =>
        new(true, command, data, null, log);

    public static AgentResult Failure(string command, string error, IReadOnlyList<string> log) =>
        new(false, command, null, error, log);
}

/// <summary>Parameter descriptor used to (a) parse CLI args, (b) emit JSON Schema for MCP.</summary>
public sealed record AgentParam(
    string Name,
    string Type,            // "string" | "number" | "boolean" | "enum"
    string Description,
    bool Required = false,
    IReadOnlyList<string>? EnumValues = null,
    object? Default = null)
{
    public object JsonSchema => new Dictionary<string, object?>
    {
        ["type"] = Type == "enum" ? "string" : Type,
        ["description"] = Description,
        ["enum"] = EnumValues,
        ["default"] = Default,
    };
}

/// <summary>
/// A single agent-callable operation. Every entry is simultaneously:
///   • a CLI verb   (vtb device.list)
///   • an MCP tool  (tool: vtb_device_list)
///   • an NL intent (matched by the router's keyword table)
/// The engines underneath are the same ones the GUI drives — one source of truth.
/// </summary>
public sealed class AgentCommand
{
    public required string Verb { get; init; }              // dot-path, e.g. "device.list"
    public required string Summary { get; init; }           // one-line description (for tool listing)
    public required string Category { get; init; }          // grouping for help output
    public IReadOnlyList<AgentParam> Params { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];   // NL router keywords (Chinese first)
    public bool Destructive { get; init; }                  // marks dangerous ops in listings
    public required Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<AgentResult>> Execute { get; init; }

    public string ToolName => "vtb_" + Verb.Replace('.', '_').Replace('-', '_');

    public object ToolSchema => new Dictionary<string, object?>
    {
        ["name"] = ToolName,
        ["description"] = Summary + (Destructive ? " [危险操作]" : ""),
        ["inputSchema"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = Params.ToDictionary(p => Camel(p.Name), p => p.JsonSchema),
            ["required"] = Params.Where(p => p.Required).Select(p => Camel(p.Name)).ToList(),
        },
    };

    public static string Camel(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

/// <summary>The single registry backing CLI, MCP, NL routing and GUI help.</summary>
public static class CommandRegistry
{
    public static readonly JsonSerializerOptions Json = new()
    {
        // snake_case for the agent surface: { ok, command, data, error, log }
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Shared log sink per invocation (thread-safe).</summary>
    public sealed class RunLog
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = [];
        public void Add(string line) { lock (_lock) _lines.Add(line); }
        public IReadOnlyList<string> Lines { get { lock (_lock) return _lines.ToArray(); } }
        public Action<string> AsAction() => Add;
    }

    private static readonly List<AgentCommand> _commands = [];
    public static IReadOnlyList<AgentCommand> Commands { get; } = _commands.AsReadOnly();

    public static void Register(AgentCommand command) => _commands.Add(command);

    public static AgentCommand? Find(string verb) =>
        _commands.FirstOrDefault(c => c.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses "--key value / --key=value / --flag" style args against a command's params.</summary>
    public static IReadOnlyDictionary<string, object?> ParseArgs(AgentCommand command, IEnumerable<string> args)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var token = list[i];
            if (!token.StartsWith("--")) continue;
            var body = token[2..];
            var eq = body.IndexOf('=');
            var key = eq >= 0 ? body[..eq] : body;
            var value = eq >= 0 ? body[(eq + 1)..]
                        : (i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "true");

            var param = command.Params.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            result[param?.Name ?? key] = Coerce(value, param?.Type);
        }
        foreach (var p in command.Params.Where(p => p.Default is not null && !result.ContainsKey(p.Name)))
            result[p.Name] = p.Default;
        return result;
    }

    private static object? Coerce(string value, string? type) => type switch
    {
        "number" => double.TryParse(value, out var d) ? d : value,
        "boolean" => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1",
        _ => value,
    };

    /// <summary>Validates required params; returns a readable error list.</summary>
    public static List<string> Validate(AgentCommand command, IReadOnlyDictionary<string, object?> args) =>
        command.Params.Where(p => p.Required && !args.ContainsKey(p.Name))
            .Select(p => $"缺少必需参数 --{p.Name}（{p.Description}）")
            .ToList();

    /// <summary>Runs a command end-to-end with validation and JSON envelope.</summary>
    public static async Task<AgentResult> RunAsync(string verb, IReadOnlyDictionary<string, object?> args,
        CancellationToken ct = default)
    {
        var command = Find(verb);
        if (command is null)
            return AgentResult.Failure(verb, $"未知命令: {verb}（vtb --list 查看全部）", []);

        var missing = Validate(command, args);
        if (missing.Count > 0)
            return AgentResult.Failure(verb, string.Join("; ", missing), []);

        try
        {
            return await command.Execute(args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentResult.Failure(verb, ex.Message, []);
        }
    }

    public static string Serialize(AgentResult result) => JsonSerializer.Serialize(result, Json);
}
