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

    /// <summary>
    /// Upstream OujiaLogicalPartitionNames (oujiaflash.cs:42-50) — the logical (dynamic)
    /// partitions that must go through FastbootD create/delete-logical-partition flows.
    /// </summary>
    public static readonly string[] LogicalPartitionNames =
    [
        "system", "odm", "vendor", "product", "system_ext", "system_dlkm",
        "vendor_dlkm", "odm_dlkm", "my_bigball", "my_carrier", "my_company",
        "my_engineering", "my_heytap", "my_manifest", "my_preload",
        "my_product", "my_region", "my_stock",
    ];

    /// <summary>Upstream image extensions accepted from a package directory.</summary>
    private static readonly string[] ImageExtensions = [".img", ".bin", ".raw", ".sparse"];

    /// <summary>UI mode labels ↔ engine modes (upstream: 普通/强力/AB通刷/仅FBD + 售后 FB/FBD).</summary>
    public static readonly (string Label, OujiaMode Mode)[] ModeTable =
    [
        ("普通线刷", OujiaMode.NormalFull),
        ("强力线刷（修复Super）", OujiaMode.StrongFull),
        ("AB 通刷", OujiaMode.AbFull),
        ("仅 FastbootD", OujiaMode.FastbootDOnly),
        ("修复 FastbootD", OujiaMode.RepairFastbootD),
        ("售后 FB 模式", OujiaMode.SvcFb),
        ("售后 FBD 模式", OujiaMode.SvcFbd),
    ];

    private static bool IsLogicalPartition(string name) => LogicalPartitionNames.Contains(name);

    /// <summary>
    /// Collects partition images from a firmware directory and orders them by FILE SIZE
    /// ascending — the upstream rule for every mode (no hardcoded partition order).
    /// Skips payload.bin and GPT dumps; _a/_b suffixes are normalized to the base name
    /// (upstream: the file suffix only names the source slot, the actual target is
    /// decided by the flow).
    /// </summary>
    public IReadOnlyList<(string Partition, string ImagePath)> ScanImages(string directory, bool dynamicOnly = false)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => ImageExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !Path.GetFileName(f).Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("gpt", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var items = new List<(string, string)>();
        foreach (var file in files)
        {
            var name = NormalizePartitionBaseName(Path.GetFileNameWithoutExtension(file));
            if (dynamicOnly && !IsLogicalPartition(name))
                continue;
            items.Add((name, file));
        }

        // Upstream: OrderBy(FileInfo.Length) — smallest images first.
        return items
            .OrderBy(i => new FileInfo(i.Item2).Length)
            .ToList();
    }

    /// <summary>Strips a trailing _a/_b slot suffix (upstream NormalizeOujiaPartitionBaseName).</summary>
    public static string NormalizePartitionBaseName(string partition)
    {
        var m = Regex.Match(partition, @"^(?<base>.+)_(?<slot>a|b)$");
        return m.Success ? m.Groups["base"].Value : partition;
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

    /// <summary>Runs a full flash flow end-to-end, following the upstream sequence.</summary>
    public async Task RunAsync(string serial, string packageDir, OujiaMode mode,
        Action<string>? log = null, IProgress<double>? progress = null,
        bool clearData = false, bool autoReboot = true,
        CancellationToken ct = default)
    {
        var device = await _fastboot.DevicesAsync(ct).ConfigureAwait(false);
        if (device.Count == 0)
            throw new InvalidOperationException("未检测到 Fastboot 设备。");

        if (mode is OujiaMode.FastbootDOnly or OujiaMode.SvcFbd or OujiaMode.RepairFastbootD)
        {
            serial = await EnsureFastbootdAsync(serial, mode == OujiaMode.FastbootDOnly, log, ct).ConfigureAwait(false);
        }

        if (mode == OujiaMode.RepairFastbootD)
        {
            // Upstream repair partition set: bootloader chain, both slots, no erase, no clear-data.
            foreach (var p in new[] { "boot", "dtbo", "init_boot", "modem", "recovery",
                                      "vbmeta", "vbmeta_system", "vbmeta_vendor", "vendor_boot" })
            {
                var img = Path.Combine(packageDir, $"{p}.img");
                if (!File.Exists(img)) { log?.Invoke($"[欧加] 警告: 未找到 {p} 镜像文件，跳过"); continue; }
                log?.Invoke($"[欧加] [Flashing] {p}.img → {p}_a.img");
                await _fastboot.FlashAsync(serial, $"{p}_a", img, ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false); // upstream: 500ms between slots
                log?.Invoke($"[欧加] [Flashing] {p}.img → {p}_b.img");
                await _fastboot.FlashAsync(serial, $"{p}_b", img, ct).ConfigureAwait(false);
            }
            // Repair ends by entering FastbootD — no clear-data, no FRP, no auto reboot.
            log?.Invoke("[欧加] reboot fastboot（进入 FastbootD 校验）…");
            await _fastboot.RebootAsync(serial, "fastboot", ct).ConfigureAwait(false);
            log?.Invoke("[欧加] ✓ 修复流程完成。");
            return;
        }

        if (mode == OujiaMode.StrongFull)
        {
            // Upstream Force mode: NO erases — rebuild super via logical-partition surgery on slot a.
            log?.Invoke("[欧加] 强力线刷：重构 super（delete/create-logical-partition）…");
            await RebuildSuperAsync(serial, "a", createSize: 40, log, ct).ConfigureAwait(false);
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

        // Upstream post-flash actions (ExecuteOujiaPostFlashActionsAsync):
        // ensure FastbootD → erase frp → optional clear-data (userdata → metadata →
        // QC-only -w → misc) → optional reboot.
        log?.Invoke("[欧加] 收尾：进入 FastbootD 擦除 FRP…");
        serial = await EnsureFastbootdAsync(serial, requireExisting: false, log, ct).ConfigureAwait(false);
        var frp = await _fastboot.EraseAsync(serial, "frp", ct).ConfigureAwait(false);
        if (!frp.Success) log?.Invoke("[欧加] ⚠ 擦除FRP失败");

        if (clearData)
        {
            log?.Invoke("[欧加] 清除数据：erase userdata → erase metadata…");
            await _fastboot.EraseAsync(serial, "userdata", ct).ConfigureAwait(false);
            await _fastboot.EraseAsync(serial, "metadata", ct).ConfigureAwait(false);
            var misc = Path.Combine(AppContext.BaseDirectory, "tmp", "misc.img");
            if (File.Exists(misc))
            {
                log?.Invoke("[欧加] flash misc（清除数据补丁镜像）…");
                await _fastboot.FlashAsync(serial, "misc", misc, ct).ConfigureAwait(false);
            }
        }

        if (autoReboot)
        {
            await _fastboot.RebootAsync(serial, "", ct).ConfigureAwait(false);
            log?.Invoke("[欧加] ✓ 全部完成，设备重启中。");
        }
        else
        {
            log?.Invoke("[欧加] ✓ 本阶段完成（未自动重启）。");
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Upstream force-mode super rebuild: delete every logical partition on both slots
    /// (tolerating "not found"), then create the target-slot partitions with the given
    /// size hint (force/仅FBD use 40, AB uses 0).
    /// </summary>
    private async Task RebuildSuperAsync(string serial, string targetSlot, int createSize,
        Action<string>? log, CancellationToken ct)
    {
        foreach (var name in LogicalPartitionNames)
        {
            foreach (var slot in new[] { "a", "b" })
            {
                var r = await _fastboot.RunAsync(serial, $"delete-logical-partition {name}_{slot}",
                    timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
                if (r.Success) log?.Invoke($"[欧加] delete-logical-partition {name}_{slot}");
            }
            var create = await _fastboot.RunAsync(serial, $"create-logical-partition {name}_{targetSlot} {createSize}",
                timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
            if (!create.Success) log?.Invoke($"[欧加] ⚠ create-logical-partition {name}_{targetSlot} 失败");
        }
        log?.Invoke("[欧加] 修复Super 完成。");
    }

    /// <summary>Upstream FastbootD gate: must already be in FastbootD when required; otherwise reboot fastboot.</summary>
    private async Task<string> EnsureFastbootdAsync(string serial, bool requireExisting,
        Action<string>? log, CancellationToken ct)
    {
        var isUserspace = await _fastboot.GetVarAsync(serial, "is-userspace", ct).ConfigureAwait(false);
        var inFastbootd = isUserspace.Contains("yes", StringComparison.OrdinalIgnoreCase)
                       || isUserspace.Contains("true", StringComparison.OrdinalIgnoreCase)
                       || isUserspace.Equals("1", StringComparison.OrdinalIgnoreCase);
        if (inFastbootd) return serial;

        if (requireExisting)
        {
            log?.Invoke("[欧加] 仅FastbootD线刷要求设备已进入FastbootD模式。");
            throw new InvalidOperationException("仅FastbootD线刷要求设备已进入 FastbootD 模式。");
        }

        log?.Invoke("[欧加] reboot fastboot（进入 FastbootD）…");
        await _fastboot.RebootAsync(serial, "fastboot", ct).ConfigureAwait(false);
        await Task.Delay(4000, ct).ConfigureAwait(false);
        return (await _fastboot.DevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault()?.Serial
            ?? throw new InvalidOperationException("进入 FastbootD 后未找到设备。");
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
