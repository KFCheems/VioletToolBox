using System.Text.RegularExpressions;
using System.Xml.Linq;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Core.Flash;

public enum OujiaMode
{
    NormalFull,      // 普通线刷
    StrongFull,      // 强力线刷（附加擦除 userdata/metadata/frp）
    AbFull,          // AB 通刷（同一镜像刷 A/B 两槽）
    FastbootDOnly,   // 仅 FastbootD 动态分区
    RepairFastbootD, // 修复 FastbootD
    SvcFb,           // 售后包 FB 模式（XML 散表）
    SvcFbd,          // 售后包 FBD 模式（fastbootd + XML 散表）
}

public sealed record OujiaFlashPlan(IReadOnlyList<(string Partition, string ImagePath)> Items);

/// <summary>
/// OPPO / OnePlus / realme line-flash engine, ported from upstream oujiaflash.cs.
/// Scans an extracted firmware directory, builds the flash plan per mode and drives
/// fastboot with progress + logging. EDL portions of the upstream engine are served
/// by <see cref="Edl.EdlSession"/>; this class owns the fastboot/fastbootd paths.
/// </summary>
public sealed class OujiaFlashService
{
    private readonly FastbootService _fastboot = new();

    /// <summary>Partition ordering used by the upstream engine (bootloader-ish first, dynamic last).</summary>
    private static readonly string[] OrderHint =
    [
        "vbmeta", "vbmeta_system", "vbmeta_vendor",
        "abl", "aop", "aop_config", "boot", "init_boot", "vendor_boot", "dtbo",
        "xbl", "xbl_config", "xbl_ramdump", "tz", "hyp", "devcfg", "keymaster", "qupfw",
        "uefisecapp", "imagefv", "shrm", "multiimgoem", "modem", "bluetooth", "dsp",
        "recovery", "misc",
    ];

    private static readonly string[] DynamicHint = ["super", "system", "vendor", "product", "odm", "my_region", "my_carrier", "my_bigball", "my_heytap", "my_stock", "my_preload", "my_product", "my_engineering", "my_company", "my_manifest"];

    /// <summary>Collects partition images from a firmware directory (skips payload.bin and cache files).</summary>
    public IReadOnlyList<(string Partition, string ImagePath)> ScanImages(string directory, bool dynamicOnly = false)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("gpt", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var items = new List<(string, string)>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (dynamicOnly && !DynamicHint.Any(h => h == name) && !name.StartsWith("_"))
                continue;
            items.Add((name, file));
        }

        return items
            .OrderBy(i =>
            {
                var idx = Array.FindIndex(OrderHint, h => i.Item1 == h);
                return idx >= 0 ? idx : 100 + StringComparer.Ordinal.GetHashCode(i.Item1) % 100;
            })
            .ToList();
    }

    /// <summary>Reads the anti-rollback (ARB) level when the bootloader exposes it.</summary>
    public async Task<string> CheckArbAsync(string serial, CancellationToken ct = default)
    {
        foreach (var variable in new[] { "anti", "arbid", "version-bootloader" })
        {
            var value = await _fastboot.GetVarAsync(serial, variable, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                && !value.Contains("unknown", StringComparison.OrdinalIgnoreCase))
                return $"{variable} = {value}";
        }
        return "无法读取 ARB（设备不暴露 anti 变量，通常代表未熔断）。";
    }

    /// <summary>Builds the flash plan for a mode. Handles AB 通刷 duplication.</summary>
    public async Task<OujiaFlashPlan> BuildPlanAsync(string serial, string packageDir, OujiaMode mode,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var dynamicOnly = mode is OujiaMode.FastbootDOnly or OujiaMode.SvcFbd;
        var images = ScanImages(packageDir, dynamicOnly);
        if (images.Count == 0)
            throw new InvalidOperationException("固件目录中没有可刷写的镜像文件。");

        var vars = await _fastboot.GetAllVarsAsync(serial, ct).ConfigureAwait(false);
        var plan = new List<(string, string)>();

        foreach (var (partition, image) in images)
        {
            if (mode == OujiaMode.AbFull && partition.EndsWith("_a", StringComparison.Ordinal))
            {
                // AB 通刷: mirror the A-slot image onto the B slot too.
                var b = partition[..^2] + "_b";
                if (vars.ContainsKey($"partition-size:{b}"))
                {
                    plan.Add((partition, image));
                    plan.Add((b, image));
                    continue;
                }
            }
            plan.Add((partition, image));
        }

        log?.Invoke($"[欧加] 计划刷写 {plan.Count} 项。");
        return new OujiaFlashPlan(plan);
    }

    /// <summary>Runs a full flash flow end-to-end.</summary>
    public async Task RunAsync(string serial, string packageDir, OujiaMode mode,
        Action<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var device = await _fastboot.DevicesAsync(ct).ConfigureAwait(false);
        if (device.Count == 0)
            throw new InvalidOperationException("未检测到 Fastboot 设备。");

        if (mode is OujiaMode.FastbootDOnly or OujiaMode.SvcFbd or OujiaMode.RepairFastbootD)
        {
            var isUserspace = await _fastboot.GetVarAsync(serial, "is-userspace", ct).ConfigureAwait(false);
            if (!isUserspace.Contains("yes", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("[欧加] 重启进入 FastbootD…");
                await _fastboot.RebootAsync(serial, "fastboot", ct).ConfigureAwait(false);
                await Task.Delay(4000, ct).ConfigureAwait(false);
                serial = (await _fastboot.DevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault()?.Serial
                    ?? throw new InvalidOperationException("进入 FastbootD 后未找到设备。");
            }
        }

        if (mode == OujiaMode.RepairFastbootD)
        {
            // 修复 FastbootD：重刷引导链后强制进入用户空间。
            foreach (var p in new[] { "boot", "init_boot", "vendor_boot", "dtbo" })
            {
                var img = Path.Combine(packageDir, $"{p}.img");
                if (File.Exists(img))
                {
                    log?.Invoke($"[欧加] flash {p}（修复引导）…");
                    var r = await _fastboot.FlashAsync(serial, p, img, ct).ConfigureAwait(false);
                    if (!r.Success) log?.Invoke($"[欧加] ✗ {p}: {r.Combined}");
                }
            }
            var vbmeta = Path.Combine(packageDir, "vbmeta.img");
            if (File.Exists(vbmeta))
            {
                log?.Invoke("[欧加] flash vbmeta --disable-verity --disable-verification…");
                var psi = ProcessRunner.MakeStartInfo(_fastboot.FastbootPath!,
                    $"-s {serial} --disable-verity --disable-verification flash vbmeta \"{vbmeta}\"");
                await ProcessRunner.RunAsync(psi, timeout: TimeSpan.FromMinutes(3), ct: ct).ConfigureAwait(false);
            }
        }

        if (mode is OujiaMode.StrongFull or OujiaMode.SvcFb or OujiaMode.SvcFbd)
        {
            foreach (var p in new[] { "userdata", "metadata", "frp" })
            {
                log?.Invoke($"[欧加] erase {p}…");
                var r = await _fastboot.EraseAsync(serial, p, ct).ConfigureAwait(false);
                if (!r.Success) log?.Invoke($"[欧加] （{p} 不支持擦除，跳过）");
            }
        }

        var plan = await BuildPlanAsync(serial, packageDir, mode, log, ct).ConfigureAwait(false);
        var done = 0;
        foreach (var (partition, image) in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"[欧加] ({done + 1}/{plan.Items.Count}) flash {partition} ← {Path.GetFileName(image)}");
            var result = await _fastboot.FlashAsync(serial, partition, image, ct).ConfigureAwait(false);
            if (!result.Success)
                throw new IOException($"刷写 {partition} 失败：{result.Combined}");
            done++;
            progress?.Report(done * 100.0 / plan.Items.Count);
        }

        if (mode is OujiaMode.NormalFull or OujiaMode.StrongFull or OujiaMode.AbFull)
        {
            // Auto-reboot when the complete flow succeeded, matching upstream 自动重启.
            await _fastboot.RebootAsync(serial, "", ct).ConfigureAwait(false);
            log?.Invoke("[欧加] ✓ 全部完成，设备重启中。");
        }
        else
        {
            log?.Invoke("[欧加] ✓ 本阶段完成（未自动重启）。");
        }
    }

    /// <summary>售后包 XML 散表刷写：解析可视刷写生成的 XML 脚本并按表执行。</summary>
    public async Task RunXmlAsync(string serial, string xmlPath, bool fastbootD,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var doc = XDocument.Load(xmlPath);
        var steps = doc.Descendants("step")
            .Select(s => new
            {
                Op = (string?)s.Attribute("operation") ?? "",
                Partition = (string?)s.Attribute("partition") ?? "",
                File = (string?)s.Attribute("file") ?? "",
            })
            .Where(s => !string.IsNullOrEmpty(s.Op))
            .ToList();

        if (steps.Count == 0)
            throw new InvalidOperationException("XML 中没有刷写步骤（期望 <step operation=…> 结构）。");

        if (fastbootD)
        {
            log?.Invoke("[欧加] FBD 模式：重启进入 FastbootD…");
            await _fastboot.RebootAsync(serial, "fastboot", ct).ConfigureAwait(false);
            await Task.Delay(4000, ct).ConfigureAwait(false);
            serial = (await _fastboot.DevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault()?.Serial ?? serial;
        }

        var i = 0;
        foreach (var s in steps)
        {
            ct.ThrowIfCancellationRequested();
            switch (s.Op.ToLowerInvariant())
            {
                case "flash" when File.Exists(s.File):
                    log?.Invoke($"[欧加] ({++i}/{steps.Count}) flash {s.Partition} ← {Path.GetFileName(s.File)}");
                    await _fastboot.FlashAsync(serial, s.Partition, s.File, ct).ConfigureAwait(false);
                    break;
                case "erase":
                    log?.Invoke($"[欧加] ({++i}/{steps.Count}) erase {s.Partition}");
                    await _fastboot.EraseAsync(serial, s.Partition, ct).ConfigureAwait(false);
                    break;
                case "reboot":
                    await _fastboot.RebootAsync(serial, s.Partition, ct).ConfigureAwait(false);
                    break;
                default:
                    log?.Invoke($"[欧加] 跳过步骤 {s.Op} {s.Partition}（文件缺失或未知操作）");
                    break;
            }
        }
        log?.Invoke("[欧加] ✓ XML 散表刷写完成。");
    }
}
