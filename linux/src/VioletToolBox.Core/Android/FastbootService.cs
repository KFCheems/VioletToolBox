using System.Text.RegularExpressions;

namespace VioletToolBox.Core.Android;

public sealed record FastbootDevice(string Serial, string State)
{
    public string DisplayName => $"{Serial} ({State})";
}

/// <summary>Fastboot orchestration over the fastboot binary resolved by <see cref="ToolLocator"/>.</summary>
public sealed class FastbootService
{
    public string? FastbootPath { get; } = ToolLocator.Find("fastboot");

    private string RequireFastboot()
    {
        if (FastbootPath is null)
            throw new InvalidOperationException("未找到 fastboot，请安装 android-tools 或配置 tools/ 目录。");
        return FastbootPath;
    }

    public async Task<List<FastbootDevice>> DevicesAsync(CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        var result = await ProcessRunner.RunAsync(fb, "devices", timeout: TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new FastbootDevice(parts[0].Trim(), parts[1].Trim()))
            .ToList();
    }

    public async Task<FastbootDevice?> FirstDeviceAsync(CancellationToken ct = default)
        => (await DevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Reads a fastboot variable. Note: fastboot prints getvar output on stderr.</summary>
    public async Task<string> GetVarAsync(string serial, string variable, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        var result = await ProcessRunner.RunAsync(fb, $"-s {serial} getvar {variable}",
            timeout: TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        var text = result.Combined;
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line, $@"^{Regex.Escape(variable)}:\s*(?<v>.+?)\s*$");
            if (m.Success) return m.Groups["v"].Value.Trim();
        }
        return string.Empty;
    }

    public async Task<Dictionary<string, string>> GetAllVarsAsync(string serial, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        var result = await ProcessRunner.RunAsync(fb, $"-s {serial} getvar all",
            timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Combined.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(?<k>[a-zA-Z0-9_:\-\.]+):\s*(?<v>.+?)\s*$");
            if (m.Success && !vars.ContainsKey(m.Groups["k"].Value))
                vars[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        return vars;
    }

    public Task<ProcessResult> FlashAsync(string serial, string partition, string imagePath, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        return ProcessRunner.RunAsync(fb, $"-s {serial} flash {partition} \"{imagePath}\"",
            timeout: TimeSpan.FromMinutes(15), ct: ct);
    }

    public Task<ProcessResult> EraseAsync(string serial, string partition, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        return ProcessRunner.RunAsync(fb, $"-s {serial} erase {partition}",
            timeout: TimeSpan.FromMinutes(3), ct: ct);
    }

    public Task<ProcessResult> FormatAsync(string serial, string partition, string? fsType = null, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        var fsArg = string.IsNullOrEmpty(fsType) ? "" : $":{fsType}";
        return ProcessRunner.RunAsync(fb, $"-s {serial} format{fsArg} {partition}",
            timeout: TimeSpan.FromMinutes(3), ct: ct);
    }

    public Task<ProcessResult> SetActiveAsync(string serial, string slot, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        return ProcessRunner.RunAsync(fb, $"-s {serial} set_active {slot}",
            timeout: TimeSpan.FromSeconds(15), ct: ct);
    }

    public Task<ProcessResult> RebootAsync(string serial, string target = "", CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        var args = string.IsNullOrEmpty(target) ? $"-s {serial} reboot" : $"-s {serial} reboot {target}";
        return ProcessRunner.RunAsync(fb, args, timeout: TimeSpan.FromSeconds(15), ct: ct);
    }

    /// <summary>Reads a partition back to a file using fastboot fetch (fastbootd / modern bootloaders).</summary>
    public Task<ProcessResult> FetchAsync(string serial, string partition, string outputPath, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        return ProcessRunner.RunAsync(fb, $"-s {serial} fetch {partition} \"{outputPath}\"",
            timeout: TimeSpan.FromMinutes(30), ct: ct);
    }

    /// <summary>Raw fastboot command passthrough (logical-partition management etc.).</summary>
    public Task<ProcessResult> RunAsync(string serial, string command,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var fb = RequireFastboot();
        return ProcessRunner.RunAsync(fb, $"-s {serial} {command}", timeout: timeout, ct: ct);
    }
}
