using System.Xml.Linq;

namespace VioletToolBox.Core.Android;

/// <summary>
/// One-stop ADB toolkit covering the misc device operations of the upstream
/// 基本刷入 / 安卓通用 tabs (FRP wipe, MTP switch, wireless ADB, OEM unlock …).
/// </summary>
public sealed class AdbToolkitService
{
    private readonly AdbService _adb = new();

    public async Task<bool> WipeFrpAsync(string serial, Action<string>? log = null, CancellationToken ct = default)
    {
        // FRP lives in the frp partition; wiping needs root (fastboot erase frp covers the rest).
        await _adb.SuShellAsync(serial, "dd if=/dev/zero of=/dev/block/by-name/frp", timeout: TimeSpan.FromMinutes(2), ct: ct)
            .ConfigureAwait(false);
        log?.Invoke("FRP 已通过 root dd 清除。");
        return true;
    }

    public Task SwitchToMtpAsync(string serial, CancellationToken ct = default)
        => _adb.ShellAsync(serial, "svc usb setFunctions mtp", ct: ct);

    public Task<ProcessResult> FixAdbAsync(CancellationToken ct = default)
        // Restart the ADB server: the standard cure for stale USB states.
        => ProcessRunner.RunAsync(_adb.AdbPath ?? "adb", "kill-server", timeout: TimeSpan.FromSeconds(15), ct: ct);

    public async Task<bool> EnableDiagPortAsync(string serial, Action<string>? log = null, CancellationToken ct = default)
    {
        // OnePlus/realme: enable the diagnostic port via the oplus diag service.
        var output = await _adb.ShellAsync(serial, "setprop sys.usb.configfs 1 && setprop sys.usb.state diag,adb", ct: ct)
            .ConfigureAwait(false);
        log?.Invoke(output.Length == 0 ? "基带调试端口指令已发送。" : output);
        return true;
    }

    public async Task<string> ReadDdrTypeAsync(string serial, CancellationToken ct = default)
    {
        // OnePlus DDR type is exposed in the oplus ddr prop chain.
        var props = await _adb.GetPropsAsync(serial, ct).ConfigureAwait(false);
        foreach (var key in new[] { "ro.vendor.oplus.ddr_type", "ro.boot.ddr_type", "ro.boot.ddr_info", "persist.sys.ddr_type" })
        {
            if (props.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Contains("5") || value == "0x200" || value == "512" ? "DDR5" : "DDR4";
        }
        return "设备未定义 DDR 类型";
    }

    public Task<string> WirelessConnectAsync(string hostPort, CancellationToken ct = default)
        => ProcessRunner.RunAsync(_adb.AdbPath ?? "adb", $"connect {hostPort}")
            .ContinueWith(t => t.Result.Combined, ct);

    public async Task<IReadOnlyList<(string Name, string Value)>> ReadOplusInfoAsync(string serial, CancellationToken ct = default)
    {
        var props = await _adb.GetPropsAsync(serial, ct).ConfigureAwait(false);
        return props
            .Where(kv => kv.Key.StartsWith("ro.vendor.oplus", StringComparison.OrdinalIgnoreCase)
                      || kv.Key.StartsWith("ro.oplus", StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(kv => kv.Key)
            .ToList();
    }

    /// <summary>Runs the bundled ocdt_tool.py (OPLUS OCDT analysis) with python3.</summary>
    public async Task<ProcessResult> AnalyzeOcdtAsync(string ocdtFilePath, string outputDir,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var python = ToolLocator.Find("python3");
        if (python is null)
            throw new InvalidOperationException("未找到 python3。");

        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "ocdt_tool.py");
        if (!File.Exists(script))
            script = Path.Combine(AppContext.BaseDirectory, "..", "VioletToolBox", "ocdt_tool.py");
        if (!File.Exists(script))
            throw new InvalidOperationException("未找到 ocdt_tool.py，请将其放入 scripts/ 目录。");

        Directory.CreateDirectory(outputDir);
        var result = await ProcessRunner.RunStreamingAsync(python, $"\"{script}\" -i \"{ocdtFilePath}\" -o \"{outputDir}\"",
            line => log?.Invoke($"[OCDT] {line}"), timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
        return result;
    }
}
