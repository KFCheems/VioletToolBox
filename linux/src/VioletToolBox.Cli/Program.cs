using System.Text.Json;
using VioletToolBox.Agent;

namespace VioletToolBox.Cli;

/// <summary>
/// vtb — the AI-native entrypoint of VioletToolBox.
///
///   vtb --list                       列出全部命令（含参数 schema）
///   vtb <verb> [--param value …]     执行命令 → JSON envelope 输出
///   vtb --schema <verb>              输出单条命令的参数 schema（机器可读）
///   vtb --mcp                        作为 MCP 服务器在 stdio 上运行
///   vtb --tools                      MCP tools/list 格式（便于 AI 预注入）
///   vtb "自然语言请求"                 NL 路由 → 候选命令与参数建议
///   vtb --raw                        输出紧凑 JSON（默认 pretty）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // stdout carries ONLY JSON (agent-friendly); diagnostics go to stderr.
        Console.Error.WriteLine($"VioletToolBox Agent CLI · {DateTime.Now:yyyy-MM-dd}");
        BuiltinCommands.RegisterAll();

        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            if (args[0] == "--mcp")
            {
                await new McpServer().RunAsync().ConfigureAwait(false);
                return 0;
            }

            if (args[0] == "--list")
            {
                var listing = CommandRegistry.Commands
                    .GroupBy(c => c.Category)
                    .Select(g => new Dictionary<string, object?>
                    {
                        ["category"] = g.Key,
                        ["commands"] = g.Select(c => new Dictionary<string, object?>
                        {
                            ["verb"] = c.Verb,
                            ["summary"] = c.Summary,
                            ["destructive"] = c.Destructive,
                            ["params"] = c.Params.Select(p => new Dictionary<string, object?>
                            {
                                ["name"] = p.Name, ["type"] = p.Type,
                                ["required"] = p.Required, ["description"] = p.Description,
                                ["enum"] = p.EnumValues, ["default"] = p.Default,
                            }).ToList(),
                        }).ToList(),
                    });
                Emit(AgentResult.Success("cli.list", listing, []));
                return 0;
            }

            if (args[0] == "--tools")
            {
                // MCP tools/list payload straight to stdout for AI preloading.
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new Dictionary<string, object?> { ["tools"] = CommandRegistry.Commands.Select(c => c.ToolSchema).ToList() },
                    CommandRegistry.Json));
                return 0;
            }

            if (args[0] == "--schema")
            {
                var verb = args.Length > 1 ? args[1] : "";
                var command = CommandRegistry.Find(verb);
                if (command is null)
                {
                    Emit(AgentResult.Failure("cli.schema", $"未知命令: {verb}", []));
                    return 2;
                }
                Emit(AgentResult.Success("cli.schema", command.ToolSchema, []));
                return 0;
            }

            if (args[0] == "--raw")
            {
                var rest = args.Skip(1).ToArray();
                return await RunCommand(rest, raw: true).ConfigureAwait(false);
            }

            // Natural language: first arg is not a known verb and not a flag.
            if (!args[0].StartsWith('-') && CommandRegistry.Find(args[0]) is null)
            {
                var utterance = string.Join(' ', args);
                Console.Error.WriteLine(NlRouter.Explain(utterance));
                var best = NlRouter.RouteBest(utterance);
                if (best is { } match && CommandRegistry.Validate(match.Command, match.Args).Count == 0)
                {
                    // Fully-specified request → execute directly (AI may chain this).
                    var result = await CommandRegistry.RunAsync(match.Command.Verb, match.Args).ConfigureAwait(false);
                    Emit(result);
                    return result.Ok ? 0 : 1;
                }
                // Ambiguous → emit routing candidates as JSON and exit non-zero.
                Emit(AgentResult.Success("cli.route",
                    new { utterance, candidates = NlRouter.Route(utterance).Select(m => new
                    {
                        verb = m.Command.Verb, score = m.Score, args = m.Args,
                    }) }, []));
                return 3; // routing-incomplete
            }

            return await RunCommand(args, raw: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Emit(AgentResult.Failure("cli", ex.Message, []));
            return 1;
        }
    }

    private static async Task<int> RunCommand(string[] args, bool raw)
    {
        var verb = args[0];
        var command = CommandRegistry.Find(verb);
        if (command is null)
        {
            Emit(AgentResult.Failure(verb, $"未知命令: {verb}（vtb --list 查看全部）", []));
            return 2;
        }
        var parsed = CommandRegistry.ParseArgs(command, args.Skip(1));
        var result = await CommandRegistry.RunAsync(verb, parsed).ConfigureAwait(false);
        Emit(result, raw);
        return result.Ok ? 0 : 1;
    }

    private static void Emit(AgentResult result, bool raw = false)
    {
        var options = raw
            ? new JsonSerializerOptions(CommandRegistry.Json) { WriteIndented = false }
            : CommandRegistry.Json;
        Console.Out.WriteLine(JsonSerializer.Serialize(result, options));
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
        vtb — 紫罗兰工具箱 · AI 原生入口
        用法:
          vtb --list                    列出全部命令（含参数）
          vtb <verb> [--参数 值 …]       执行命令，输出 JSON envelope
          vtb --schema <verb>           单命令 schema
          vtb --tools                   MCP tools/list 格式（AI 注入用）
          vtb --mcp                     作为 MCP 服务器运行（stdio）
          vtb "自然语言"                  意图路由（完全确定时直接执行）
          vtb --raw <verb> …            紧凑 JSON 输出（管道场景）
        envelope: { ok, command, data, error, log[] }
        退出码: 0 成功 · 1 执行失败 · 2 未知命令 · 3 意图不完整
        """);
    }
}
