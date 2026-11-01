using System.Text.RegularExpressions;

namespace VioletToolBox.Core.Android;

public sealed record AppInfo(string Package, string Version, bool IsSystem)
{
    public string DisplayName => IsSystem ? $"[系统] {Package}" : Package;
}

/// <summary>App management + freeze/unfreeze over ADB (应用管理).</summary>
public sealed class AppManagementService
{
    private readonly AdbService _adb = new();

    public async Task<List<AppInfo>> ListThirdPartyAsync(string serial, CancellationToken ct = default)
    {
        var out3 = await _adb.ShellAsync(serial, "pm list packages -3", ct: ct).ConfigureAwait(false);
        var versions = await ReadVersionsAsync(serial, out3, ct).ConfigureAwait(false);
        return out3.Split('\n')
            .Select(l => Regex.Match(l, @"package:(?<p>\S+)").Groups["p"].Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => new AppInfo(p, versions.GetValueOrDefault(p, "—"), IsSystem: false))
            .OrderBy(a => a.Package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<AppInfo>> ListSystemAsync(string serial, CancellationToken ct = default)
    {
        var outs = await _adb.ShellAsync(serial, "pm list packages -s", ct: ct).ConfigureAwait(false);
        return outs.Split('\n')
            .Select(l => Regex.Match(l, @"package:(?<p>\S+)").Groups["p"].Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => new AppInfo(p, "—", IsSystem: true))
            .OrderBy(a => a.Package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, string>> ReadVersionsAsync(string serial, string packageList, CancellationToken ct)
    {
        var versions = new Dictionary<string, string>();
        // dumpsys is one round-trip; parse "Package [name] ... versionName=x".
        var dump = await _adb.ShellAsync(serial, "dumpsys package", timeout: TimeSpan.FromSeconds(30), ct: ct)
            .ConfigureAwait(false);
        var current = string.Empty;
        foreach (var line in dump.Split('\n'))
        {
            var m = Regex.Match(line, @"Package \[(?<p>[^\]]+)\]");
            if (m.Success) current = m.Groups["p"].Value;
            var v = Regex.Match(line, @"versionName=(?<v>[\d\.]+)");
            if (m.Success || (v.Success && current.Length > 0))
            {
                if (v.Success && !versions.ContainsKey(current)) versions[current] = v.Groups["v"].Value;
                if (m.Success && !versions.ContainsKey(current)) versions[current] = "—";
            }
        }
        return versions;
    }

    public Task<ProcessResult> InstallApkAsync(string serial, string apkPath, CancellationToken ct = default)
        => ProcessRunner.RunAsync(_adb.AdbPath ?? "adb", $"-s {serial} install -r \"{apkPath}\"",
            timeout: TimeSpan.FromMinutes(5), ct: ct);

    public Task UninstallAsync(string serial, string package, CancellationToken ct = default)
        => _adb.ShellAsync(serial, $"pm uninstall -k --user 0 {package}", ct: ct);

    public Task FreezeAsync(string serial, string package, CancellationToken ct = default)
        => _adb.ShellAsync(serial, $"pm suspend {package}", ct: ct);

    public Task UnfreezeAsync(string serial, string package, CancellationToken ct = default)
        => _adb.ShellAsync(serial, $"pm unsuspend {package}", ct: ct);

    public Task ClearDataAsync(string serial, string package, CancellationToken ct = default)
        => _adb.ShellAsync(serial, $"pm clear {package}", ct: ct);

    public async Task DisableRootAsync(string serial, string package, Action<string>? log = null, CancellationToken ct = default)
    {
        // Hide from the user profile AND the work profile where present.
        await _adb.ShellAsync(serial, $"pm disable-user --user 0 {package}", ct: ct).ConfigureAwait(false);
        log?.Invoke($"已冻结 {package}（隐藏环境用：pm disable-user）。");
    }

    // ---- file transfer (文件传输) ----

    public async Task<IReadOnlyList<string>> ListSdcardAsync(string serial, string subPath = "", CancellationToken ct = default)
    {
        var path = "/sdcard/" + subPath.Trim('/');
        var listing = await _adb.ShellAsync(serial, $"ls -1a \"{path}\"", ct: ct).ConfigureAwait(false);
        return listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n is not "." and not "..")
            .OrderBy(n => n)
            .ToList();
    }

    public Task<ProcessResult> PushAsync(string serial, string localPath, string remotePath, CancellationToken ct = default)
        => ProcessRunner.RunAsync(_adb.AdbPath ?? "adb", $"-s {serial} push \"{localPath}\" \"{remotePath}\"",
            timeout: TimeSpan.FromMinutes(30), ct: ct);

    public Task<ProcessResult> PullAsync(string serial, string remotePath, string localPath, CancellationToken ct = default)
        => ProcessRunner.RunAsync(_adb.AdbPath ?? "adb", $"-s {serial} pull \"{remotePath}\" \"{localPath}\"",
            timeout: TimeSpan.FromMinutes(30), ct: ct);

    public Task DeleteRemoteAsync(string serial, string remotePath, CancellationToken ct = default)
        => _adb.ShellAsync(serial, $"rm -rf \"{remotePath}\"", ct: ct);
}
