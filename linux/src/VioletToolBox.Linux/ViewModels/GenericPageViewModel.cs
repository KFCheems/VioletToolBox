using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using VioletToolBox.Core;
using VioletToolBox.Core.Android;
using VioletToolBox.Core.Flash;

namespace VioletToolBox.Linux.ViewModels;

/// <summary>安卓通用: 卡刷转线刷 / TWRP 引导 / OCDT 分析 / 修复ADB / 无线调试.</summary>
public sealed class GenericPageViewModel : ViewModelBase
{
    private readonly AdbToolkitService _toolkit = new();
    private readonly AdbService _adb = new();
    private readonly FastbootService _fastboot = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private string _zipPath = string.Empty;
    private string _outputDir = string.Empty;
    private string _ocdtPath = string.Empty;
    private string _twrpImg = string.Empty;
    private string _wirelessAddr = string.Empty;
    private string _status = "卡刷包（含 payload.bin 或散 img）可转为线刷目录并生成 fastboot 脚本。";

    public GenericPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        PickZipCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择卡刷包 zip", "zip").ConfigureAwait(true);
            if (f is not null) ZipPath = f;
        });
        ConvertCommand = new AsyncCommand(() => RunAsync(ConvertAsync));
        BootTwrpCommand = new AsyncCommand(() => RunAsync(BootTwrpAsync));
        PickOcdtCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择 OCDT 文件", "xml", "bin").ConfigureAwait(true);
            if (f is not null) OcdtPath = f;
        });
        AnalyzeOcdtCommand = new AsyncCommand(() => RunAsync(AnalyzeOcdtAsync));
        FixAdbCommand = new AsyncCommand(() => RunAsync(FixAdbAsync));
        WirelessConnectCommand = new AsyncCommand(() => RunAsync(WirelessConnectAsync));
    }

    public AsyncCommand PickZipCommand { get; }
    public AsyncCommand ConvertCommand { get; }
    public AsyncCommand BootTwrpCommand { get; }
    public AsyncCommand PickOcdtCommand { get; }
    public AsyncCommand AnalyzeOcdtCommand { get; }
    public AsyncCommand FixAdbCommand { get; }
    public AsyncCommand WirelessConnectCommand { get; }

    public string ZipPath { get => _zipPath; set => SetProperty(ref _zipPath, value); }
    public string OutputDir { get => _outputDir; set => SetProperty(ref _outputDir, value); }
    public string OcdtPath { get => _ocdtPath; set => SetProperty(ref _ocdtPath, value); }
    public string TwrpImg { get => _twrpImg; set => SetProperty(ref _twrpImg, value); }
    public string WirelessAddr { get => _wirelessAddr; set => SetProperty(ref _wirelessAddr, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    /// <summary>卡刷转线刷：解包 zip，展开 payload.bin（若存在）到线刷目录并生成 flash_all.txt。</summary>
    private async Task ConvertAsync()
    {
        if (!File.Exists(ZipPath)) throw new InvalidOperationException("请选择卡刷包。");
        var outDir = string.IsNullOrWhiteSpace(OutputDir)
            ? Path.Combine(Path.GetDirectoryName(ZipPath)!, Path.GetFileNameWithoutExtension(ZipPath) + "_lineflash")
            : OutputDir;
        Directory.CreateDirectory(outDir);
        Status = "解压卡刷包…";
        await ProcessRunner.RunAsync("unzip", $"-o \"{ZipPath}\" -d \"{outDir}\"", timeout: TimeSpan.FromMinutes(10))
            .ConfigureAwait(true);
        _log("[通用] 卡刷包已解压。");

        var payload = Path.Combine(outDir, "payload.bin");
        if (File.Exists(payload))
        {
            Status = "解出 payload 分区…";
            var reader = await Core.Payload.PayloadProcessing.OpenSourceAsync(payload, default).ConfigureAwait(true);
            var (payloadReader, _) = await Core.Payload.PayloadProcessing.OpenPayloadReaderAsync(reader, default,
                log: m => _log($"[通用] {m}")).ConfigureAwait(true);
            var ctx = await Core.Payload.PayloadProcessing.ReadManifestAsync(payloadReader, default).ConfigureAwait(true);
            var names = ctx.Manifest.Partitions.Select(p => p.PartitionName).Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
            var progress = new Progress<(long Done, long Total)>(p => Status = $"解包 {p.Done * 100.0 / Math.Max(1, p.Total):F1}%");
            await Core.Payload.PayloadProcessing.ExtractPartitionsAsync(ctx, names, outDir, 4,
                m => _log($"[通用] {m}"), progress, default).ConfigureAwait(true);
        }

        // 生成 fastboot 线刷脚本（txt，fastboot 命令逐行）。
        var scriptPath = Path.Combine(outDir, "flash_all.txt");
        var engine = new OujiaFlashService();
        var images = engine.ScanImages(outDir);
        await using var writer = new StreamWriter(scriptPath);
        await writer.WriteLineAsync("# 由紫罗兰工具箱 Linux 版生成 — fastboot 刷写脚本").ConfigureAwait(true);
        await writer.WriteLineAsync("# 在 fastboot 模式下逐行执行，或使用欧加线刷页选择本目录。").ConfigureAwait(true);
        foreach (var (partition, image) in images)
            await writer.WriteLineAsync($"fastboot flash {partition} \"{Path.GetFileName(image)}\"").ConfigureAwait(true);
        await writer.WriteLineAsync("fastboot reboot").ConfigureAwait(true);

        Status = $"✓ 线刷目录已生成：{outDir}（含 flash_all.txt，共 {images.Count} 个镜像）";
        _log($"[通用] {Status}");
    }

    private async Task BootTwrpAsync()
    {
        if (!File.Exists(TwrpImg)) throw new InvalidOperationException("请选择 TWRP recovery 镜像。");
        var fb = await _fastboot.FirstDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 Fastboot 设备。");
        Status = "临时引导 TWRP…";
        var r = await ProcessRunner.RunAsync(_fastboot.FastbootPath!, $"-s {fb.Serial} boot \"{TwrpImg}\"",
            timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(true);
        Status = r.Success ? "✓ TWRP 已引导（重启后失效）。" : $"✗ {r.Combined}";
        _log($"[通用] {Status}");
    }

    private async Task AnalyzeOcdtAsync()
    {
        if (!File.Exists(OcdtPath)) throw new InvalidOperationException("请选择 OCDT 文件。");
        var outDir = Path.Combine(Path.GetDirectoryName(OcdtPath)!, "ocdt_out");
        var r = await _toolkit.AnalyzeOcdtAsync(OcdtPath, outDir, m => _log(m)).ConfigureAwait(true);
        Status = r.Success ? $"✓ OCDT 分析完成：{outDir}" : $"✗ {r.Combined}";
    }

    private async Task FixAdbAsync()
    {
        var r = await _toolkit.FixAdbAsync().ConfigureAwait(true);
        Status = r.Success ? "✓ adb 服务已重启。" : $"✗ {r.Combined}";
    }

    private async Task WirelessConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(WirelessAddr)) throw new InvalidOperationException("请输入 IP:端口。");
        var r = await _toolkit.WirelessConnectAsync(WirelessAddr.Trim()).ConfigureAwait(true);
        Status = $"无线调试：{r}";
        _log($"[通用] {Status}");
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[通用] {ex}"); }
    }
}
