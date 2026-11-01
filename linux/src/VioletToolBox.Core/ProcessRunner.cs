using System.Diagnostics;
using System.Text;

namespace VioletToolBox.Core;

/// <summary>Result of a completed external process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : $"{StdOut}\n{StdErr}";
}

/// <summary>
/// Thin async wrapper over Process, shared by every service in the toolbox so that
/// output encoding, cancellation and timeout behave identically everywhere.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Test seam: when non-null, all launches are routed here instead of spawning real
    /// processes. The consistency suite uses this to record command sequences and
    /// script device responses without hardware.
    /// </summary>
    public static Func<string, string, Task<ProcessResult>>? Intercept { get; set; }

    public static ProcessStartInfo MakeStartInfo(string exe, string args, string? workDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        return psi;
    }

    public static async Task<ProcessResult> RunAsync(
        string exe, string args, string? workDir = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (Intercept is not null)
        {
            var intercepted = await Intercept(exe, args).ConfigureAwait(false);
            return intercepted;
        }
        var psi = MakeStartInfo(exe, args, workDir);
        return await RunAsync(psi, timeout, ct).ConfigureAwait(false);
    }

    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo psi, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (Intercept is not null)
        {
            var intercepted = await Intercept(psi.FileName, psi.Arguments).ConfigureAwait(false);
            return intercepted;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            return new ProcessResult(-1, string.Empty, "进程启动失败", false);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) timeoutCts.CancelAfter(timeout.Value);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            // Drain remaining buffers asynchronously to avoid pipe deadlock.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillAsync(process).ConfigureAwait(false);
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs a process, forwarding each stdout line (and stderr lines prefixed) to a callback.</summary>
    public static async Task<ProcessResult> RunStreamingAsync(
        string exe, string args, Action<string> onLine,
        string? workDir = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = MakeStartInfo(exe, args, workDir);
        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };

        if (!process.Start())
            return new ProcessResult(-1, string.Empty, "进程启动失败", false);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) timeoutCts.CancelAfter(timeout.Value);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty, string.Empty, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillAsync(process).ConfigureAwait(false);
            return new ProcessResult(-1, string.Empty, string.Empty, true);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Process may have exited between check and kill.
        }
    }
}
