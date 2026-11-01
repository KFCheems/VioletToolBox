using System.Text.Json;

namespace VioletToolBox.Agent;

/// <summary>
/// Natural-language router: maps free-form (mainly Chinese) requests to registry verbs
/// and fills obvious parameters. Deterministic keyword scoring — no network, no LLM
/// required at runtime; an AI agent calling the CLI/MCP surface can also pre-route
/// itself, in which case this is the fallback for humans.
/// </summary>
public static class NlRouter
{
    public sealed record RouteMatch(
        double Score,
        AgentCommand Command,
        IReadOnlyDictionary<string, object?> Args,
        string Explanation);

    /// <summary>Ranks commands by keyword coverage against the utterance.</summary>
    public static List<RouteMatch> Route(string utterance, int top = 3)
    {
        var text = utterance.Trim();
        var scored = new List<(double Score, AgentCommand Cmd)>();

        foreach (var command in CommandRegistry.Commands)
        {
            var score = 0.0;
            foreach (var keyword in command.Keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    score += keyword.Length; // longer keyword = stronger signal
            }
            // Verb tail match ("payload.analyze" ← "分析payload")
            var tail = command.Verb.Split('.')[^1];
            if (text.Contains(tail, StringComparison.OrdinalIgnoreCase)) score += tail.Length * 0.6;
            if (score > 0) scored.Add((score, command));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(top)
            .Select(s => new RouteMatch(
                s.Score,
                s.Cmd,
                GuessArgs(s.Cmd, text),
                $"匹配 {s.Cmd.Verb}（关键词命中得分 {s.Score:F0}）"))
            .ToList();
    }

    /// <summary>Best single route or a friendly failure with suggestions.</summary>
    public static RouteMatch? RouteBest(string utterance)
    {
        var matches = Route(utterance, 1);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Extracts obvious arguments from the utterance (paths, partition names, URLs).</summary>
    private static IReadOnlyDictionary<string, object?> GuessArgs(AgentCommand command, string text)
    {
        var args = new Dictionary<string, object?>();

        // Absolute paths → fill the first string param named file/image/zip/source/dir/out.
        var path = System.Text.RegularExpressions.Regex.Match(text, @"(?:/[\w.\-\u4e00-\u9fff]+)+");
        if (path.Success)
        {
            foreach (var name in new[] { "image", "file", "zip", "source", "dir", "out", "programmer" })
            {
                var param = command.Params.FirstOrDefault(p => p.Name == name);
                if (param is not null && !args.ContainsKey(name))
                {
                    args[name] = path.Value;
                    break;
                }
            }
        }

        // URLs fill "source".
        if (command.Params.Any(p => p.Name == "source")
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"https?://\S+"))
        {
            args["source"] = System.Text.RegularExpressions.Regex.Match(text, @"https?://\S+").Value;
        }

        // Enum params: match any enum value mentioned in the text.
        foreach (var param in command.Params.Where(p => p.EnumValues is { Count: > 0 }))
        {
            var hit = param.EnumValues!.FirstOrDefault(v =>
                !string.IsNullOrEmpty(v) && text.Contains(v, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) args[param.Name] = hit;
        }

        // Known partition words fill "partition".
        // ( fails between CJK and ASCII — use a simple contains scan, longest first.)
        if (command.Params.Any(p => p.Name == "partition"))
        {
            foreach (var name in new[] { "init_boot", "vendor_boot", "boot", "dtbo", "vbmeta",
                                         "recovery", "modem", "super", "userdata", "metadata" })
            {
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    args["partition"] = name.ToLowerInvariant();
                    break;
                }
            }
        }

        return args;
    }

    /// <summary>Human-facing reply: the routed command, missing params, and how to run it.</summary>
    public static string Explain(string utterance)
    {
        var matches = Route(utterance);
        if (matches.Count == 0)
            return $"未能理解请求。可用的能力域：{string.Join("、", CommandRegistry.Commands.Select(c => c.Category).Distinct())}\n" +
                   "示例：\"检测设备\" / \"解析payload /tmp/ota.zip\" / \"刷入boot\" / \"列出分区表\"";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"理解意图 → 命中 {matches.Count} 个候选：");
        foreach (var m in matches)
        {
            sb.AppendLine($"  {m.Command.Verb,-24} {m.Explanation}");
            var missing = CommandRegistry.Validate(m.Command, m.Args);
            if (missing.Count > 0)
                sb.AppendLine($"    待补参数: {string.Join("; ", missing)}");
            else
                sb.AppendLine($"    可直接执行: vtb {m.Command.Verb} {FormatArgs(m.Args)}");
        }
        return sb.ToString();
    }

    private static string FormatArgs(IReadOnlyDictionary<string, object?> args) =>
        string.Join(" ", args.Select(kv => $"--{kv.Key} \"{kv.Value}\""));
}
