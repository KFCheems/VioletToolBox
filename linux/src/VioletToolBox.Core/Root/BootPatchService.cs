using System.Diagnostics;
using System.Text.RegularExpressions;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Core.Root;

public sealed record BootPatchResult(string OutputPath, string Scheme);

/// <summary>
/// 脱机修补 (offline boot patching) with host-side magiskboot, kptools and kpimg.
/// Magisk-style ramdisk patching, APatch (KernelPatch) kernel patching and KernelSU
/// LKM flows all run natively on Linux here — the Windows build bundled .exe ports of
/// the same tools.
/// </summary>
public sealed class BootPatchService
{
    private static string? Tool(string name)
    {
        var path = ToolLocator.Find(name);
        if (path is not null) ToolLocator.EnsureExecutable(path);
        return path;
    }

    /// <summary>Repacks a boot image after running an arbitrary magiskboot command chain.</summary>
    public async Task<BootPatchResult> PatchWithApatchAsync(string bootImg, string superKey,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var kptools = Tool("kptools") ?? throw new InvalidOperationException("未找到 kptools（tools/ 目录）。");
        var kpimg = Tool("kpimg-android") ?? throw new InvalidOperationException("未找到 kpimg-android（tools/ 目录）。");

        var outPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(bootImg)) ?? ".",
            Path.GetFileNameWithoutExtension(bootImg) + "_apatch.img");

        log?.Invoke("[修补] kptools -p（APatch 内核补丁）…");
        var args = $"-p -i \"{bootImg}\" -k \"{kpimg}\" -K \"{superKey}\" -o \"{outPath}\"";
        var result = await ProcessRunner.RunAsync(kptools, args, timeout: TimeSpan.FromMinutes(5), ct: ct)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new IOException($"kptools 失败：{result.Combined}");
        log?.Invoke($"[修补] ✓ 已生成 {Path.GetFileName(outPath)}");
        return new BootPatchResult(outPath, "APatch/KernelPatch");
    }

    /// <summary>Unpacks and inspects a boot image; returns kernel/ramdisk metadata.</summary>
    public async Task<string> InspectAsync(string bootImg, string workDir, CancellationToken ct = default)
    {
        var magiskboot = Tool("magiskboot") ?? throw new InvalidOperationException("未找到 magiskboot（tools/ 目录）。");
        Directory.CreateDirectory(workDir);
        foreach (var stale in new[] { "kernel", "ramdisk.cpio", "header" })
            File.Delete(Path.Combine(workDir, stale));

        var psi = ProcessRunner.MakeStartInfo(magiskboot, $"unpack \"{bootImg}\"");
        psi.WorkingDirectory = workDir;
        var result = await ProcessRunner.RunAsync(psi, timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new IOException($"magiskboot unpack 失败：{result.Combined}");
        return result.Combined;
    }

    /// <summary>Magisk-style patch: unpack, patch ramdisk (skip_initramfs etc.), repack.</summary>
    public async Task<BootPatchResult> PatchWithMagiskAsync(string bootImg, string workDir,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var magiskboot = Tool("magiskboot") ?? throw new InvalidOperationException("未找到 magiskboot（tools/ 目录）。");
        Directory.CreateDirectory(workDir);

        async Task<int> Run(string args)
        {
            var psi = ProcessRunner.MakeStartInfo(magiskboot, args);
            psi.WorkingDirectory = workDir;
            var r = await ProcessRunner.RunAsync(psi, timeout: TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
            if (!r.Success) throw new IOException($"magiskboot {args.Split(' ')[0]} 失败：{r.Combined}");
            return r.ExitCode;
        }

        log?.Invoke("[修补] unpack…");
        await Run($"unpack \"{bootImg}\"").ConfigureAwait(false);
        if (File.Exists(Path.Combine(workDir, "ramdisk.cpio")))
        {
            log?.Invoke("[修补] cpio test/patch…");
            await Run("cpio ramdisk.cpio \"add 750 init magiskinit\" \"patch\"").ConfigureAwait(false);
        }
        log?.Invoke("[修补] repack…");
        var outPath = Path.Combine(workDir, "new-boot.img");
        await Run($"repack \"{bootImg}\" \"{outPath}\"").ConfigureAwait(false);
        log?.Invoke($"[修补] ✓ 已生成 {outPath}");
        return new BootPatchResult(outPath, "Magisk");
    }

    /// <summary>KernelSU LKM flow: run the device-side ksud via adb root on the phone.</summary>
    public async Task<bool> InstallLkmOnDeviceAsync(string serial, string lkmPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var adb = new AdbService();
        var remote = "/data/local/tmp/vtb_lkm.ko";
        var push = await ProcessRunner.RunAsync(adb.AdbPath ?? "adb", $"-s {serial} push \"{lkmPath}\" {remote}",
            ct: ct).ConfigureAwait(false);
        if (!push.Success) throw new IOException($"LKM 推送失败：{push.Combined}");
        var insmod = await adb.SuShellAsync(serial, $"insmod {remote}", ct: ct).ConfigureAwait(false);
        log?.Invoke($"[修补] insmod 输出：{insmod}");
        return true;
    }
}

/// <summary>
/// 模块专区: manager APK quick-install, module zip install on device, root hiding.
/// </summary>
public sealed class ModuleService
{
    private static readonly (string Name, string Repo, string AssetPattern)[] Managers =
    [
        ("Magisk", "topjohnwu/Magisk", "Magisk-v"),
        ("KernelSU", "tiann/KernelSU", "KernelSU"),
        ("SukiSU", "SukiSU-Ultra/SukiSU-Ultra", "SukiSU"),
        ("APatch", "bmax121/APatch", "APatch"),
    ];

    private readonly AdbService _adb = new();

    public IReadOnlyList<string> ListManagers() => Managers.Select(m => m.Name).ToList();

    /// <summary>Fetches the newest manager APK download URL from the manager's GitHub releases.</summary>
    public async Task<string?> ResolveManagerApkUrlAsync(string managerName, CancellationToken ct = default)
    {
        var manager = Managers.FirstOrDefault(m => m.Name.Equals(managerName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(manager.Name))
            throw new InvalidOperationException($"未知管理器：{managerName}");
        var release = await GitHub.LatestReleaseAsync(manager.Repo, ct).ConfigureAwait(false);
        if (release is null) return null;
        var (tag, assets) = release.Value;
        var asset = assets.FirstOrDefault(a => a.Contains(manager.AssetPattern, StringComparison.OrdinalIgnoreCase)
                                           && a.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                 ?? assets.FirstOrDefault(a => a.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        return asset is null ? null : $"https://github.com/{manager.Repo}/releases/download/{tag}/{asset}";
    }

    public Task InstallApkAsync(string serial, string apkPath, CancellationToken ct = default)
        => new AppManagementService().InstallApkAsync(serial, apkPath, ct);

    /// <summary>Installs a module zip on-device through the manager CLI (needs root).</summary>
    public async Task<string> InstallModuleZipAsync(string serial, string moduleZipPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var remote = "/data/local/tmp/vtb_module.zip";
        var push = await ProcessRunner.RunAsync(_adb.AdbPath ?? "adb", $"-s {serial} push \"{moduleZipPath}\" {remote}",
            ct: ct).ConfigureAwait(false);
        if (!push.Success) throw new IOException($"模块推送失败：{push.Combined}");

        // KernelSU ksud first, Magisk CLI as fallback — mirrors upstream's SukiSU/KSU preference.
        var output = await _adb.SuShellAsync(serial,
            "ksud module install /data/local/tmp/vtb_module.zip || magisk --install-module /data/local/tmp/vtb_module.zip",
            timeout: TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
        await _adb.ShellAsync(serial, $"rm -f {remote}", ct: ct).ConfigureAwait(false);
        log?.Invoke(output);
        return output;
    }

    /// <summary>Hidden-environment helpers (隐藏ROOT): denylist / umount tricks, device-side.</summary>
    public async Task<IReadOnlyList<string>> HideRootAsync(string serial, CancellationToken ct = default)
    {
        var results = new List<string>();
        foreach (var (label, cmd) in new[]
                 {
                     ("magisk denylist enable", "magisk --denylist enable"),
                     ("magisk denylist add lsposed", "magisk --denylist add com.android.shell"),
                     ("detach stock mount points", "magisk --sqlite \"UPDATE settings SET value=1 WHERE key=\\\"denylist\\\"\""),
                 })
        {
            try
            {
                var output = await _adb.SuShellAsync(serial, cmd, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
                results.Add($"{label}: {(output.Length == 0 ? "已执行" : output)}");
            }
            catch (Exception ex)
            {
                results.Add($"{label}: 跳过（{ex.Message}）");
            }
        }
        return results;
    }
}

/// <summary>Minimal GitHub release helpers shared by module/download features.</summary>
public static class GitHub
{
    public static async Task<(string Tag, IReadOnlyList<string> Assets)?> LatestReleaseAsync(string repo, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VioletToolBox-Linux");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = doc.RootElement.TryGetProperty("assets", out var arr)
            ? arr.EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? "").ToList()
            : [];
        return (tag, assets);
    }
}
