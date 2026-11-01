using System.Diagnostics;
using System.IO.Compression;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Core.Flash;

/// <summary>
/// Xiaomi official line-flash. Xiaomi's official fastboot packages ship Linux scripts
/// (flash_all.sh / flash_all_except_storage.sh / flash_all_lock.sh), so the Linux port
/// runs them natively with bash — no Windows cmd emulation needed.
/// </summary>
public sealed class XiaomiFlashService
{
    private readonly FastbootService _fastboot = new();

    public static readonly string[] ScriptNames =
    [
        "flash_all.sh",
        "flash_all_except_storage.sh",
        "flash_all_except_data_storage.sh",
        "flash_all_lock.sh",
    ];

    public static bool LooksLikeXiaomiPackage(string directory) =>
        ScriptNames.Any(n => File.Exists(Path.Combine(directory, n)));

    /// <summary>Lists the flash scripts available inside an extracted package directory.</summary>
    public IReadOnlyList<string> ListScripts(string packageDir)
        => ScriptNames.Where(n => File.Exists(Path.Combine(packageDir, n))).ToList();

    /// <summary>Extracts a Xiaomi fastboot package (.tgz / .tar.gz / .zip).</summary>
    public async Task<string> ExtractPackageAsync(string archivePath, string outputDir,
        Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();

        if (ext is ".tgz" or ".gz")
        {
            log?.Invoke($"[小米] 解包 {Path.GetFileName(archivePath)}（tar.gz，首次解包较慢）…");
            await ProcessRunner.RunAsync("tar", $"-xzf \"{archivePath}\" -C \"{outputDir}\"",
                timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);
        }
        else if (ext == ".zip")
        {
            log?.Invoke($"[小米] 解包 {Path.GetFileName(archivePath)}…");
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, outputDir, overwriteFiles: true), ct)
                .ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("小米线刷包应为 .tgz 或 .zip 格式。");
        }

        // The archive usually wraps a single directory like "images/".
        if (LooksLikeXiaomiPackage(outputDir)) return outputDir;
        foreach (var sub in Directory.EnumerateDirectories(outputDir))
        {
            if (LooksLikeXiaomiPackage(sub)) return sub;
            foreach (var subSub in Directory.EnumerateDirectories(sub))
                if (LooksLikeXiaomiPackage(subSub)) return subSub;
        }
        throw new InvalidOperationException("包内未找到 flash_all*.sh，可能不是官方线刷包。");
    }

    /// <summary>Runs the chosen script with the bundled/system fastboot on PATH.</summary>
    public async Task<ProcessResult> RunScriptAsync(string packageDir, string scriptName,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var script = Path.Combine(packageDir, scriptName);
        if (!File.Exists(script))
            throw new FileNotFoundException("线刷脚本不存在。", script);
        if ((await _fastboot.DevicesAsync(ct).ConfigureAwait(false)).Count == 0)
            throw new InvalidOperationException("未检测到 Fastboot 设备，请先重启到 Bootloader。");

        ToolLocator.EnsureExecutable(script);
        // The upstream scripts invoke plain `fastboot`; expose our resolved binary via PATH
        // plus the tools dir so both are visible.
        var fastbootDir = Path.GetDirectoryName(_fastboot.FastbootPath) ?? "/usr/bin";
        var envPath = $"{fastbootDir}:{string.Join(':', ToolLocator.SearchRoots())}:/usr/local/bin:/usr/bin";
        log?.Invoke($"[小米] 执行 {scriptName} …");

        var psi = ProcessRunner.MakeStartInfo("bash", $"\"{script}\"");
        psi.EnvironmentVariables["PATH"] = envPath;
        psi.WorkingDirectory = packageDir;

        var result = await ProcessRunner.RunStreamingAsync(psi.FileName, psi.Arguments,
            line => log?.Invoke($"[小米] {line}"), ct: ct).ConfigureAwait(false);
        log?.Invoke(result.Success ? "[小米] ✓ 刷写完成。" : $"[小米] ✗ 脚本退出码 {result.ExitCode}");
        return result;
    }
}
