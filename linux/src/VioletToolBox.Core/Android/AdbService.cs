using System.Text.RegularExpressions;

namespace VioletToolBox.Core.Android;

public sealed record AdbDevice(string Serial, string State, string Model, string Device)
{
    public bool IsOnline => State == "device";
    public string DisplayName => string.IsNullOrEmpty(Model) ? Serial : $"{Model} ({Serial})";
}

/// <summary>ADB orchestration over the adb binary resolved by <see cref="ToolLocator"/>.</summary>
public sealed class AdbService
{
    public string? AdbPath { get; } = ToolLocator.Find("adb");

    private string RequireAdb()
    {
        if (AdbPath is null)
            throw new InvalidOperationException("未找到 adb，请安装 android-tools 或配置 tools/ 目录。");
        return AdbPath;
    }

    public async Task EnsureServerAsync(CancellationToken ct = default)
    {
        var adb = RequireAdb();
        await ProcessRunner.RunAsync(adb, "start-server", timeout: TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
    }

    public async Task<List<AdbDevice>> DevicesAsync(CancellationToken ct = default)
    {
        var adb = RequireAdb();
        var result = await ProcessRunner.RunAsync(adb, "devices -l", timeout: TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        var devices = new List<AdbDevice>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            // adb devices -l: "serial  state  device:product model:… device:…"
            var m = Regex.Match(line, @"^(?<serial>\S+)\s+(?<state>\S+)(?<rest>.*)$");
            if (!m.Success) continue;
            var rest = m.Groups["rest"].Value;
            var model = Regex.Match(rest, @"model:(?<m>\S+)").Groups["m"].Value;
            var dev = Regex.Match(rest, @"device:(?<d>\S+)").Groups["d"].Value;
            devices.Add(new AdbDevice(m.Groups["serial"].Value, m.Groups["state"].Value, model, dev));
        }
        return devices;
    }

    public async Task<AdbDevice?> FirstOnlineDeviceAsync(CancellationToken ct = default)
        => (await DevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.IsOnline);

    /// <summary>Runs a shell command on a specific device and returns stdout.</summary>
    public async Task<string> ShellAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var adb = RequireAdb();
        var result = await ProcessRunner.RunAsync(adb, $"-s {serial} shell {Quote(command)}", timeout: timeout, ct: ct).ConfigureAwait(false);
        return result.StdOut.TrimEnd();
    }

    /// <summary>Runs a shell command as root via su; throws when su is unavailable.</summary>
    public async Task<string> SuShellAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!await HasRootAsync(serial, ct).ConfigureAwait(false))
            throw new InvalidOperationException("设备无 root（su）权限，无法执行该操作。");
        // Upstream wraps su payloads in single quotes: shell "su -c 'cmd'".
        var escaped = command.Replace("'", "'\\''");
        return await ShellAsync(serial, $"su -c '{escaped}'", timeout, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasRootAsync(string serial, CancellationToken ct = default)
    {
        var adb = RequireAdb();
        var result = await ProcessRunner.RunAsync(adb, $"-s {serial} shell su -c id", timeout: TimeSpan.FromSeconds(8), ct: ct).ConfigureAwait(false);
        return result.Success && result.StdOut.Contains("uid=0");
    }

    public Task RebootAsync(string serial, string target = "", CancellationToken ct = default)
    {
        var adb = RequireAdb();
        var args = string.IsNullOrEmpty(target) ? $"-s {serial} reboot" : $"-s {serial} reboot {target}";
        return ProcessRunner.RunAsync(adb, args, timeout: TimeSpan.FromSeconds(15), ct: ct);
    }

    public async Task<Dictionary<string, string>> GetPropsAsync(string serial, CancellationToken ct = default)
    {
        var adb = RequireAdb();
        var result = await ProcessRunner.RunAsync(adb, $"-s {serial} shell getprop", timeout: TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StdOut.Split('\n'))
        {
            var m = Regex.Match(line, @"^\[(?<key>[^\]]+)\]:\s*\[(?<val>.*)\]");
            if (m.Success) props[m.Groups["key"].Value] = m.Groups["val"].Value;
        }
        return props;
    }

    private static string Quote(string arg)
    {
        var escaped = arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
