using System.Diagnostics;
using System.Text;

using VioletToolBox.Core;
using Xunit;

// The harness mutates the static ProcessRunner.Intercept / ToolLocator.ResolverOverride seams,
// so test classes must run serially (see assembly-level DisableTestParallelization).
namespace VioletToolBox.ConsistencyTests;

/// <summary>
/// Records every external invocation the engines attempt and routes them to a
/// scripted fake adb/fastboot, so command sequences can be asserted against the
/// upstream Windows build's ground truth without any device attached.
/// </summary>
public sealed class CommandHarness : IDisposable
{
    private readonly CommandRecorder _recorder = new();
    private readonly FakeDevice _device;

    /// <summary>Log lines emitted via the engine's log callbacks.</summary>
    public List<string> Log { get; } = [];

    public CommandHarness()
    {
        _device = new FakeDevice(_recorder);
        ProcessRunner.Intercept = async (exe, args) =>
        {
            var (code, stdout, stderr) = _device.Respond(exe, args);
            await Task.CompletedTask.ConfigureAwait(false);
            return new ProcessResult(code, stdout, stderr, false);
        };
        ToolLocator.ResolverOverride = name =>
            name switch
            {
                "adb" => "/fake/bin/adb",
                "fastboot" => "/fake/bin/fastboot",
                "aria2c" => "/fake/bin/aria2c",
                "scrcpy" => "/fake/bin/scrcpy",
                "magiskboot" => "/fake/bin/magiskboot",
                "kptools" => "/fake/bin/kptools",
                "kpimg-android" => "/fake/bin/kpimg-android",
                "lpmake" => "/fake/bin/lpmake",
                _ => $"/fake/bin/{name}",
            };
    }

    public IReadOnlyList<CommandRecorder.Invocation> Invocations => _recorder.Invocations;

    /// <summary>Full flattened script ("adb -s S …" per line, exe basename) for sequence assertions.</summary>
    public IReadOnlyList<string> Script => _recorder.Invocations
        .Select(i => $"{Path.GetFileName(i.Exe)} {i.Args}".Trim())
        .ToList();

    /// <summary>Assert helper: normalized subsequence containment ignoring quoting/space.</summary>
    public void AssertScriptContains(params string[] expectedLines)
    {
        var script = Script;
        foreach (var expected in expectedLines)
        {
            Assert.True(script.Any(l => Normalize(l).Contains(Normalize(expected))),
                $"缺少命令行: {expected}\n实际序列:\n{string.Join('\n', script)}");
        }
    }

    public FakeDevice Device => _device;

    /// <summary>Assert helper: ordered subsequence check (commands appear in this relative order).</summary>
    public void AssertScriptOrder(params string[] expectedLines)
    {
        var script = Script.Select(Normalize).ToList();
        var cursor = 0;
        foreach (var expected in expectedLines.Select(Normalize))
        {
            var found = script.FindIndex(cursor, l => l.Contains(expected));
            Assert.True(found >= 0,
                $"命令 '{expected}' 未按序出现 (从 {cursor} 行起)\n实际序列:\n{string.Join('\n', Script)}");
            cursor = found + 1;
        }
    }

    private static string Normalize(string s) =>
        // Whitespace-collapse and strip shell quoting so `"cmd"` matches `cmd`.
        string.Join(' ', s.Replace("\"", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public void Dispose()
    {
        ProcessRunner.Intercept = null;
        ToolLocator.ResolverOverride = null;
    }
}
