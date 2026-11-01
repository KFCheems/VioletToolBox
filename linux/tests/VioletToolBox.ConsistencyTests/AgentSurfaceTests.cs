using System.Text.Json;
using VioletToolBox.Agent;
using Xunit;

namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// AI-surface tests: registry shape, MCP JSON-RPC protocol, NL routing.
/// These verify the contracts an AI agent (CLI shell-out or MCP client) relies on.
/// </summary>
public class AgentSurfaceTests
{
    static AgentSurfaceTests()
    {
        BuiltinCommands.RegisterAll();
    }

    [Fact]
    public void Registry_HasDurableCommandSet()
    {
        var verbs = CommandRegistry.Commands.Select(c => c.Verb).ToHashSet();
        foreach (var expected in new[]
                 {
                     "device.list", "device.info", "device.reboot",
                     "flash.partition", "flash.repair-fastbootd",
                     "partition.list", "partition.read",
                     "payload.analyze", "payload.extract", "extract.ofp", "extract.ops",
                     "edl.ports", "edl.info", "edl.partitions",
                     "module.detect", "module.install",
                     "tools.check",
                 })
            Assert.Contains(expected, verbs);
    }

    [Fact]
    public void ToolNames_AreMcpSafe()
    {
        // MCP tool names: lowercase letters, digits, underscore only.
        foreach (var command in CommandRegistry.Commands)
        {
            Assert.Matches(@"^[a-z0-9_]+$", command.ToolName);
            Assert.StartsWith("vtb_", command.ToolName);
        }
    }

    [Fact]
    public void ToolSchemas_AreWellFormedJson()
    {
        foreach (var command in CommandRegistry.Commands)
        {
            var json = JsonSerializer.Serialize(command.ToolSchema);
            using var doc = JsonDocument.Parse(json); // must round-trip
            var root = doc.RootElement;
            Assert.Equal(command.ToolName, root.GetProperty("name").GetString());
            var schema = root.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void ArgParsing_SupportsKeyValueAndFlags()
    {
        var command = CommandRegistry.Find("device.reboot")!;
        var args = CommandRegistry.ParseArgs(command, new[] { "--target", "recovery" });
        Assert.Equal("recovery", args["target"]);

        var args2 = CommandRegistry.ParseArgs(command, new[] { "--target=edl" });
        Assert.Equal("edl", args2["target"]);

        var command2 = CommandRegistry.Find("partition.list")!;
        var args3 = CommandRegistry.ParseArgs(command2, Array.Empty<string>());
        Assert.Equal("adb", args3["mode"]); // default applied
    }

    [Fact]
    public void Validation_ReportsMissingRequiredParams()
    {
        var command = CommandRegistry.Find("flash.partition")!;
        var missing = CommandRegistry.Validate(command, new Dictionary<string, object?>());
        Assert.Contains(missing, m => m.Contains("partition"));
        Assert.Contains(missing, m => m.Contains("image"));
    }

    [Fact]
    public void UnknownVerb_FailsCleanly()
    {
        var result = CommandRegistry.RunAsync("no.such.command",
            new Dictionary<string, object?>()).GetAwaiter().GetResult();
        Assert.False(result.Ok);
        Assert.Contains("未知命令", result.Error);
    }

    [Fact]
    public void NlRouter_MatchesChineseIntents()
    {
        var best = NlRouter.RouteBest("检测设备");
        Assert.NotNull(best);
        Assert.Equal("device.list", best!.Command.Verb);

        var flash = NlRouter.RouteBest("我想刷入boot镜像");
        Assert.NotNull(flash);
        Assert.Equal("flash.partition", flash!.Command.Verb);
        Assert.Equal("boot", flash.Args["partition"]);

        var payload = NlRouter.RouteBest("解析payload清单");
        Assert.NotNull(payload);
        Assert.Equal("payload.analyze", payload!.Command.Verb);
    }

    [Fact]
    public void NlRouter_ExtractsPathArgument()
    {
        var match = NlRouter.RouteBest("解析 /tmp/roms/ota.zip")!;
        Assert.Equal("payload.analyze", match.Command.Verb);
        Assert.Equal("/tmp/roms/ota.zip", match.Args["source"]);
    }

    [Fact]
    public async Task McpServer_HandlesProtocol()
    {
        using var input = new StringReader(string.Join('\n',
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list" }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 3, method = "tools/call",
                @params = new { name = "vtb_device_list", arguments = new { } } }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 4, method = "no/such/method" })));
        var output = new StringWriter();

        var lines = new List<string>();
        var consoleOut = Console.Out;
        Console.SetOut(new LineTee(output));
        try
        {
            var server = new McpServer(input, new StringWriter());
            // Capture stdout via a redirected console: run once with our tee.
            await RunWithStdout(server, lines);
        }
        finally { Console.SetOut(consoleOut); }

        Assert.True(lines.Count >= 3, $"expected >=3 responses, got {lines.Count}");
        var init = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("violettoolbox", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        var toolCall = JsonDocument.Parse(lines[2]).RootElement;
        var text = toolCall.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var envelope = JsonDocument.Parse(text).RootElement;
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("device.list", envelope.GetProperty("command").GetString());

        var error = JsonDocument.Parse(lines[^1]).RootElement;
        Assert.Equal(-32601, error.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static async Task RunWithStdout(McpServer server, List<string> sink)
    {
        var original = Console.Out;
        await using var tee = new LineCollector(sink);
        Console.SetOut(tee);
        try { await server.RunAsync(); }
        finally { Console.SetOut(original); }
    }

    private sealed class LineTee(TextWriter inner) : TextWriter
    {
        public override System.Text.Encoding Encoding => inner.Encoding;
        public override void Write(char value) => inner.Write(value);
        public override void WriteLine(string? value) { inner.WriteLine(value); }
    }

    private sealed class LineCollector(List<string> sink) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) { if (value is not null) sink.Add(value); }
        public override void Write(string? value) { /* buffer partials into last line */ }
    }
}
