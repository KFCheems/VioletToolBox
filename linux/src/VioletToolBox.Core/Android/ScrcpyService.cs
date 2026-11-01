using System.Diagnostics;
using System.Text;

namespace VioletToolBox.Core.Android;

public sealed record ScrcpyOptions(
    int MaxSize,
    int VideoBitRateKbps,
    int MaxFps,
    bool TurnScreenOff,
    bool StayAwake,
    bool ShowTouches,
    bool Wireless)
{
    public static ScrcpyOptions Default { get; } = new(0, 8000, 0, false, false, false, false);
}

/// <summary>
/// Screen mirroring driven by the system scrcpy. The upstream Windows build shipped a
/// forked scrcpy.exe; on Linux the stock scrcpy (dnf install scrcpy) exposes the same
/// encoder pipeline, so options map 1:1 and the server binary travels with the RPM.
/// </summary>
public sealed class ScrcpyService
{
    public string? ScrcpyPath { get; } = ToolLocator.Find("scrcpy");

    public bool IsAvailable => ScrcpyPath is not null;

    /// <summary>Launches scrcpy against a serial (or the only connected device).</summary>
    public Process Launch(string? serial, ScrcpyOptions options, Action<string>? log = null)
    {
        if (ScrcpyPath is null)
            throw new InvalidOperationException("未找到 scrcpy，请先安装：sudo dnf install scrcpy");

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(serial)) sb.Append($"-s {serial} ");
        if (options.MaxSize > 0) sb.Append($"--max-size {options.MaxSize} ");
        if (options.VideoBitRateKbps > 0) sb.Append($"--video-bit-rate {options.VideoBitRateKbps}M ");
        if (options.MaxFps > 0) sb.Append($"--max-fps {options.MaxFps} ");
        if (options.TurnScreenOff) sb.Append("--turn-screen-off ");
        if (options.StayAwake) sb.Append("--stay-awake ");
        if (options.ShowTouches) sb.Append("--show-touches ");
        if (options.Wireless) sb.Append("--tcpip ");

        var psi = ProcessRunner.MakeStartInfo(ScrcpyPath, sb.ToString().TrimEnd());
        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException("scrcpy 启动失败。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}
