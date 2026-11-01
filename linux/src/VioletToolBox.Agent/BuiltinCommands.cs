using VioletToolBox.Core;
using VioletToolBox.Core.Android;
using VioletToolBox.Core.Edl;
using VioletToolBox.Core.Extraction;
using VioletToolBox.Core.Flash;
using VioletToolBox.Core.Payload;
using VioletToolBox.Core.Root;

namespace VioletToolBox.Agent;

/// <summary>
/// Catalog of every agent-callable operation. Registered once at process start;
/// CLI, MCP and the NL router all consume this list.
/// </summary>
public static class BuiltinCommands
{
    private static string? FirstAdbSerial()
        => new AdbService().FirstOnlineDeviceAsync().ConfigureAwait(false).GetAwaiter().GetResult()?.Serial;

    public static void RegisterAll()
    {
        // ---------- device ----------
        Registry(new()
        {
            Verb = "device.list", Category = "device", Summary = "列出 ADB / Fastboot 设备与连接状态",
            Keywords = ["设备", "连接", "检测设备", "devices", "看看手机"],
            Execute = async (_, ct) =>
            {
                var adb = new AdbService();
                var adbDevices = await adb.DevicesAsync(ct).ConfigureAwait(false);
                var fbDevices = await new FastbootService().DevicesAsync(ct).ConfigureAwait(false);
                return AgentResult.Success("device.list", new
                {
                    adb = adbDevices.Select(d => new { d.Serial, d.State, d.Model, d.Device }),
                    fastboot = fbDevices.Select(d => new { d.Serial, d.State }),
                }, []);
            },
        });

        Registry(new()
        {
            Verb = "device.info", Category = "device", Summary = "读取设备属性摘要（型号/系统/槽位/解锁状态）",
            Params = [new("serial", "string", "设备序列号（缺省取第一台在线设备）", Required: false)],
            Keywords = ["设备信息", "型号", "系统版本", "info", "手机信息"],
            Execute = async (args, ct) =>
            {
                var serial = args.GetValueOrDefault("serial") as string ?? FirstAdbSerial()
                    ?? throw new InvalidOperationException("未检测到 ADB 在线设备");
                var adb = new AdbService();
                var props = await adb.GetPropsAsync(serial, ct).ConfigureAwait(false);
                string Get(string k) => props.TryGetValue(k, out var v) ? v : null!;
                return AgentResult.Success("device.info", new
                {
                    serial,
                    model = Get("ro.product.model"),
                    brand = Get("ro.product.brand"),
                    android = Get("ro.build.version.release"),
                    slot = Get("ro.boot.slot_suffix"),
                    bootloaderUnlocked = Get("ro.boot.unlocked") is "1" ? true
                                         : Get("ro.boot.flash.locked") is "0" ? true : null as bool?,
                    selinux = Get("getenforce"),
                }, []);
            },
        });

        Registry(new()
        {
            Verb = "device.reboot", Category = "device", Summary = "重启设备到指定模式",
            Destructive = true,
            Params =
            [
                new("target", "enum", "重启目标", Required: true,
                    EnumValues: ["", "recovery", "bootloader", "fastbootd", "sideload", "edl"]),
                new("serial", "string", "设备序列号（可选）", Required: false),
            ],
            Keywords = ["重启", "reboot", "进入recovery", "进fastboot", "9008"],
            Execute = async (args, ct) =>
            {
                var target = args.GetValueOrDefault("target") as string ?? "";
                var serial = args.GetValueOrDefault("serial") as string ?? FirstAdbSerial();
                if (serial is null)
                {
                    var fb = await new FastbootService().DevicesAsync(ct).ConfigureAwait(false);
                    serial = fb.FirstOrDefault()?.Serial ?? throw new InvalidOperationException("未检测到设备");
                    var r = await new FastbootService().RebootAsync(serial, target, ct).ConfigureAwait(false);
                    return AgentResult.Success("device.reboot", new { serial, mode = "fastboot", ok = r.Success }, []);
                }
                await new AdbService().RebootAsync(serial, target, ct).ConfigureAwait(false);
                return AgentResult.Success("device.reboot", new { serial, mode = "adb", target }, []);
            },
        });

        // ---------- flash ----------
        Registry(new()
        {
            Verb = "flash.partition", Category = "flash", Summary = "刷入镜像到指定分区（fastboot 优先，root dd 回退）",
            Destructive = true,
            Params =
            [
                new("partition", "string", "目标分区名（boot/init_boot/dtbo/vbmeta…）", Required: true),
                new("image", "string", "本地镜像文件路径", Required: true),
                new("serial", "string", "设备序列号（可选）", Required: false),
            ],
            Keywords = ["刷入", "刷写", "flash", "刷boot"],
            Execute = async (args, ct) =>
            {
                var partition = (args.GetValueOrDefault("partition") as string)!;
                var image = (args.GetValueOrDefault("image") as string)!;
                if (!File.Exists(image))
                    return AgentResult.Failure("flash.partition", $"镜像文件不存在: {image}", []);

                var fb = new FastbootService();
                var fbDevices = await fb.DevicesAsync(ct).ConfigureAwait(false);
                if (fbDevices.Count > 0)
                {
                    var r = await fb.FlashAsync(fbDevices[0].Serial, partition, image, ct).ConfigureAwait(false);
                    return AgentResult.Success("flash.partition",
                        new { serial = fbDevices[0].Serial, channel = "fastboot", partition, ok = r.Success, output = r.Combined.Trim() }, []);
                }

                var adbSerial = FirstAdbSerial() ?? throw new InvalidOperationException("未检测到设备");
                var log = new CommandRegistry.RunLog();
                var partitions = new PartitionService();
                await partitions.WriteAdbPartitionAsync(adbSerial, partition, image, null, ct).ConfigureAwait(false);
                return AgentResult.Success("flash.partition",
                    new { serial = adbSerial, channel = "adb-root-dd", partition, ok = true }, log.Lines);
            },
        });

        Registry(new()
        {
            Verb = "flash.repair-fastbootd", Category = "flash", Summary = "修复 FastbootD（双槽重刷引导链）",
            Destructive = true,
            Params =
            [
                new("dir", "string", "固件目录（含 boot/dtbo/vbmeta 等镜像）", Required: true),
            ],
            Keywords = ["修复fastbootd", "救砖", "repair"],
            Execute = async (args, ct) =>
            {
                var dir = (args.GetValueOrDefault("dir") as string)!;
                var log = new CommandRegistry.RunLog();
                var fb = await new FastbootService().FirstDeviceAsync(ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("未检测到 Fastboot 设备");
                await new OujiaFlashService().RunAsync(fb.Serial, dir, OujiaMode.RepairFastbootD,
                    log.AsAction(), null, clearData: false, autoReboot: false, ct).ConfigureAwait(false);
                return AgentResult.Success("flash.repair-fastbootd", new { serial = fb.Serial, ok = true }, log.Lines);
            },
        });

        // ---------- partitions ----------
        Registry(new()
        {
            Verb = "partition.list", Category = "partition", Summary = "读取设备分区表（ADB root 或 Fastboot）",
            Params =
            [
                new("mode", "enum", "读取通道", EnumValues: ["adb", "fastboot"],
                    Default: "adb", Required: true),
                new("serial", "string", "设备序列号（可选）", Required: false),
            ],
            Keywords = ["分区表", "分区", "partitions"],
            Execute = async (args, ct) =>
            {
                var mode = (args.GetValueOrDefault("mode") as string ?? "adb").ToLowerInvariant();
                var svc = new PartitionService();
                var list = mode == "fastboot"
                    ? await svc.ListFastbootPartitionsAsync(
                        (await new FastbootService().FirstDeviceAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("未检测到 Fastboot 设备")).Serial, ct).ConfigureAwait(false)
                    : await svc.ListAdbPartitionsAsync(
                        FirstAdbSerial() ?? throw new InvalidOperationException("未检测到 ADB 设备"), ct).ConfigureAwait(false);
                return AgentResult.Success("partition.list",
                    list.Select(p => new { name = p.Name, size = p.SizeBytes, sizeDisplay = p.SizeDisplay, source = p.Source }), []);
            },
        });

        Registry(new()
        {
            Verb = "partition.read", Category = "partition", Summary = "读取分区到本地文件（ADB root，带进度）",
            Params =
            [
                new("partition", "string", "分区名", Required: true),
                new("out", "string", "输出文件路径", Required: true),
                new("serial", "string", "设备序列号（可选）", Required: false),
            ],
            Keywords = ["读分区", "回读", "备份分区"],
            Execute = async (args, ct) =>
            {
                var partition = (args.GetValueOrDefault("partition") as string)!;
                var outPath = (args.GetValueOrDefault("out") as string)!;
                var serial = args.GetValueOrDefault("serial") as string ?? FirstAdbSerial()
                    ?? throw new InvalidOperationException("未检测到 ADB 设备");
                var log = new CommandRegistry.RunLog();
                var progress = new Progress<PartitionProgress>(p =>
                    log.Add($"{p.Message} {p.Percent:F1}%"));
                await new PartitionService().ReadAdbPartitionAsync(serial, partition, outPath, progress, ct).ConfigureAwait(false);
                return AgentResult.Success("partition.read",
                    new { partition, output = outPath, bytes = new FileInfo(outPath).Length }, log.Lines);
            },
        });

        // ---------- payload / extraction ----------
        Registry(new()
        {
            Verb = "payload.analyze", Category = "payload", Summary = "解析 payload.bin / OTA zip 清单（支持本地与 URL）",
            Params = [new("source", "string", "payload.bin / zip 路径或 URL", Required: true)],
            Keywords = ["解析payload", "payload清单", "分析固件", "解析", "分析"],
            Execute = async (args, ct) =>
            {
                var source = (args.GetValueOrDefault("source") as string)!;
                var reader = await PayloadProcessing.OpenSourceAsync(source, ct).ConfigureAwait(false);
                var (payloadReader, _) = await PayloadProcessing.OpenPayloadReaderAsync(reader, ct).ConfigureAwait(false);
                var ctx = await PayloadProcessing.ReadManifestAsync(payloadReader, ct).ConfigureAwait(false);
                return AgentResult.Success("payload.analyze", new
                {
                    source,
                    blockSize = ctx.BlockSize,
                    partitions = ctx.Manifest.Partitions.Select(p => p.PartitionName).Where(n => !string.IsNullOrEmpty(n)),
                }, []);
            },
        });

        Registry(new()
        {
            Verb = "payload.extract", Category = "payload", Summary = "从 payload/zip 解出指定分区镜像",
            Params =
            [
                new("source", "string", "payload.bin / zip / URL", Required: true),
                new("out", "string", "输出目录", Required: true),
                new("partitions", "string", "逗号分隔的分区列表（空=全部）", Required: false),
            ],
            Keywords = ["解包payload", "提取分区", "解出"],
            Execute = async (args, ct) =>
            {
                var source = (args.GetValueOrDefault("source") as string)!;
                var outDir = (args.GetValueOrDefault("out") as string)!;
                var requested = (args.GetValueOrDefault("partitions") as string)?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var reader = await PayloadProcessing.OpenSourceAsync(source, ct).ConfigureAwait(false);
                var (payloadReader, _) = await PayloadProcessing.OpenPayloadReaderAsync(reader, ct).ConfigureAwait(false);
                var ctx = await PayloadProcessing.ReadManifestAsync(payloadReader, ct).ConfigureAwait(false);

                var all = ctx.Manifest.Partitions.Select(p => p.PartitionName).Where(n => !string.IsNullOrEmpty(n));
                var targets = requested is { Count: > 0 } ? all.Where(requested.Contains).ToHashSet() : all.ToHashSet();

                var log = new CommandRegistry.RunLog();
                Directory.CreateDirectory(outDir);
                await PayloadProcessing.ExtractPartitionsAsync(ctx, targets, outDir, workers: 4,
                    log.AsAction(), new Progress<(long Done, long Total)>(_ => { }), ct).ConfigureAwait(false);
                var written = Directory.GetFiles(outDir, "*.img").Select(f => Path.GetFileName(f)).ToList();
                return AgentResult.Success("payload.extract", new { output = outDir, images = written, count = written.Count }, log.Lines);
            },
        });

        Registry(new()
        {
            Verb = "extract.ofp", Category = "payload", Summary = "提取 OPPO/一加 .ofp 固件包",
            Params =
            [
                new("file", "string", ".ofp 文件路径", Required: true),
                new("out", "string", "输出目录", Required: true),
            ],
            Keywords = ["解包ofp", "ofp提取"],
            Execute = async (args, ct) =>
            {
                var file = (args.GetValueOrDefault("file") as string)!;
                var outDir = (args.GetValueOrDefault("out") as string)!;
                var log = new CommandRegistry.RunLog();
                await OppoOfpExtractor.ExtractAsync(file, outDir,
                    new Progress<OfpExtractProgress>(p => log.Add($"{p.Message} {p.Percent:F1}%")), ct).ConfigureAwait(false);
                return AgentResult.Success("extract.ofp", new { output = outDir }, log.Lines);
            },
        });

        Registry(new()
        {
            Verb = "extract.ops", Category = "payload", Summary = "提取一加 .ops 售后包",
            Params =
            [
                new("file", "string", ".ops 文件路径", Required: true),
                new("out", "string", "输出目录", Required: true),
            ],
            Keywords = ["解包ops", "ops提取"],
            Execute = async (args, ct) =>
            {
                var file = (args.GetValueOrDefault("file") as string)!;
                var outDir = (args.GetValueOrDefault("out") as string)!;
                var log = new CommandRegistry.RunLog();
                await OnePlusOpsExtractor.ExtractAsync(file, outDir,
                    new Progress<OpsExtractProgress>(p => log.Add($"{p.Message} {p.Percent:F1}%")), ct).ConfigureAwait(false);
                return AgentResult.Success("extract.ops", new { output = outDir }, log.Lines);
            },
        });

        // ---------- edl ----------
        Registry(new()
        {
            Verb = "edl.ports", Category = "edl", Summary = "列出可用 EDL (9008) 串口",
            Keywords = ["串口", "edl端口", "9008端口"],
            Execute = async (_, ct) =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                return AgentResult.Success("edl.ports", new { ports = EdlSession.ListEdlPorts() }, []);
            },
        });

        Registry(new()
        {
            Verb = "edl.info", Category = "edl", Summary = "连接 9008 设备并读取 Sahara 身份信息",
            Params =
            [
                new("port", "string", "串口（如 /dev/ttyUSB0）", Required: true),
                new("programmer", "string", "firehose programmer 路径", Required: true),
            ],
            Keywords = ["edl信息", "sahara", "连接9008"],
            Execute = async (args, ct) =>
            {
                var port = (args.GetValueOrDefault("port") as string)!;
                var programmer = (args.GetValueOrDefault("programmer") as string)!;
                var log = new CommandRegistry.RunLog();
                using var session = await Task.Run(() => EdlSession.Connect(port, programmer, log.AsAction()), ct).ConfigureAwait(false);
                return AgentResult.Success("edl.info", new
                {
                    port,
                    msmHwid = session.DeviceInfo.MsmHwid,
                    oemPkHash = session.DeviceInfo.OemPkHash,
                    serial = session.DeviceInfo.SerialNum,
                    sblVersion = session.DeviceInfo.SblVersion,
                }, log.Lines);
            },
        });

        Registry(new()
        {
            Verb = "edl.partitions", Category = "edl", Summary = "连接 9008 并读取全 LUN 分区表",
            Params =
            [
                new("port", "string", "串口", Required: true),
                new("programmer", "string", "firehose programmer 路径", Required: true),
            ],
            Keywords = ["edl分区表", "9008分区"],
            Execute = async (args, ct) =>
            {
                var port = (args.GetValueOrDefault("port") as string)!;
                var programmer = (args.GetValueOrDefault("programmer") as string)!;
                var log = new CommandRegistry.RunLog();
                using var session = await Task.Run(() => EdlSession.Connect(port, programmer, log.AsAction()), ct).ConfigureAwait(false);
                var partitions = session.GetPartitions(log.AsAction());
                return AgentResult.Success("edl.partitions",
                    partitions.Select(p => new { label = p.Label, lun = p.Lun, startSector = p.StartSector, sizeBytes = p.SizeBytes }), log.Lines);
            },
        });

        // ---------- module / root ----------
        Registry(new()
        {
            Verb = "module.detect", Category = "module", Summary = "探测设备 root 管理器（ksud/apd/magisk）",
            Params = [new("serial", "string", "设备序列号（可选）", Required: false)],
            Keywords = ["root管理器", "管理器检测", "什么root"],
            Execute = async (args, ct) =>
            {
                var serial = args.GetValueOrDefault("serial") as string ?? FirstAdbSerial()
                    ?? throw new InvalidOperationException("未检测到 ADB 设备");
                var manager = await new ModuleService().DetectManagerAsync(serial, ct).ConfigureAwait(false);
                return AgentResult.Success("module.detect", new
                {
                    serial,
                    manager,
                    label = manager switch { "ksud" => "KernelSU / SukiSU", "apd" => "APatch", "magisk" => "Magisk", _ => "未检测到" },
                }, []);
            },
        });

        Registry(new()
        {
            Verb = "module.install", Category = "module", Summary = "安装模块 zip 到设备（需 root）",
            Params =
            [
                new("zip", "string", "模块 zip 路径", Required: true),
                new("serial", "string", "设备序列号（可选）", Required: false),
            ],
            Keywords = ["装模块", "安装模块", "module"],
            Execute = async (args, ct) =>
            {
                var zip = (args.GetValueOrDefault("zip") as string)!;
                var serial = args.GetValueOrDefault("serial") as string ?? FirstAdbSerial()
                    ?? throw new InvalidOperationException("未检测到 ADB 设备");
                var log = new CommandRegistry.RunLog();
                var output = await new ModuleService().InstallModuleZipAsync(serial, zip, log.AsAction(), ct).ConfigureAwait(false);
                return AgentResult.Success("module.install",
                    new { serial, ok = ModuleService.LooksLikeModuleSuccess(output), output = output.Trim() }, log.Lines);
            },
        });

        // ---------- tools / env ----------
        Registry(new()
        {
            Verb = "tools.check", Category = "env", Summary = "检查外部工具链可用性（adb/fastboot/scrcpy/aria2c…）",
            Keywords = ["环境", "工具检查", "依赖"],
            Execute = async (_, ct) =>
            {
                await Task.CompletedTask.ConfigureAwait(false);
                var report = ToolLocator.Describe().Select(t => new
                {
                    name = t.Name,
                    found = t.Path is not null,
                    path = t.Path,
                });
                return AgentResult.Success("tools.check", new
                {
                    tools = report,
                    missing = ToolLocator.Describe().Where(t => t.Path is null).Select(t => t.Name).ToList(),
                }, []);
            },
        });

        void Registry(AgentCommand command) => CommandRegistry.Register(command);
    }
}
