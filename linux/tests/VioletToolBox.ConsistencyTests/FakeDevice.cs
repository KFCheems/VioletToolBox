using System.Diagnostics;

using System.Text;

namespace VioletToolBox.ConsistencyTests;

/// <summary>Scripted fake adb/fastboot responses (parsed by CommandHarness).</summary>
public sealed class FakeDevice
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);
    private readonly CommandRecorder _recorder;

    public FakeDevice(CommandRecorder recorder) => _recorder = recorder;

    public FakeDevice WithVar(string key, string value) { _vars[key] = value; return this; }
    public FakeDevice WithProp(string key, string value) { _props[key] = value; return this; }

    /// <summary>Interprets an adb/fastboot invocation and returns (exitCode, stdout, stderr).</summary>
    public (int, string, string) Respond(string exe, string args)
    {
        _recorder.Record(exe, args);
        var isAdb = exe.Contains("adb");

        if (args.Contains("devices"))
            return (0, isAdb ? "List of devices attached\nTESTSERIAL\tdevice\n" : "TESTSERIAL\tfastboot\n", "");

        if (!isAdb && args.Contains("getvar"))
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var idx = Array.IndexOf(parts, "getvar");
            var variable = idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : "";
            if (variable == "all")
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in _vars) sb.AppendLine($"{k}:{v}");
                sb.AppendLine("(bootloader) Finished. Total time: 0.001s");
                return (0, "", sb.ToString());
            }
            return _vars.TryGetValue(variable, out var val)
                ? (0, "", $"{variable}: {val}")
                : (0, "", $"{variable}: FAILED (GetVar not found)");
        }

        if (isAdb && args.Contains("getprop"))
        {
            var sb = new StringBuilder();
            foreach (var (k, v) in _props) sb.AppendLine($"[{k}]: [{v}]");
            return (0, sb.ToString(), "");
        }

        if (isAdb && args.Contains("su -c id"))
            return (0, "uid=0(root) gid=0(root) groups=0(root)", "");

        // Root manager probes (upstream: ls /data/adb/ksud | apd | magisk.db).
        if (isAdb && args.Contains("ls /data/adb/ksud"))
            return (0, "/data/adb/ksud", "");
        if (isAdb && args.Contains("ls /data/adb/apd"))
            return (0, "", "");
        if (isAdb && args.Contains("ls /data/adb/magisk.db"))
            return (0, "", "");

        // Module install commands report success the way managers do.
        if (isAdb && (args.Contains("module install") || args.Contains("install-module")))
            return (0, "- Successfully installed module", "");
        if (isAdb && args.Contains("content query"))
            return (0, "Row: 0 _id=1, display_name=测试联系人, data1=13800138000", "");
        if (isAdb && args.Contains(" rm "))
            return (0, "", "");

        if (isAdb && args.Contains("reboot"))
            return (0, "", "");

        if (!isAdb && args.Contains("reboot"))
            return (0, "", "OKAY");

        // fastboot flash/erase/set_active all print OKAY on stderr.
        if (!isAdb && (args.Contains("flash") || args.Contains("erase") || args.Contains("set_active") || args.Contains("format")))
            return (0, "", "OKAY");

        return (0, "", "");
    }
}
