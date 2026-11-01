using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core;
using VioletToolBox.Core.Android;
using VioletToolBox.Core.Flash;

namespace VioletToolBox.Linux.ViewModels;

public sealed class FlashPageViewModel : ViewModelBase
{
    private readonly AdbService _adb = new();
    private readonly FastbootService _fastboot = new();
    private readonly PartitionService _partitions = new();
    private readonly AdbToolkitService _toolkit = new();
    private readonly XiaomiFlashService _xiaomi = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    public static readonly string[] CommonPartitions =
    [
        "boot", "init_boot", "dtbo", "vendor_boot", "vbmeta", "recovery",
        "modem", "abl", "aop", "xbl", "xbl_config", "tz", "hyp", "devcfg",
        "boot_a", "init_boot_a", "dtbo_a", "vendor_boot_a", "vbmeta_a",
        "boot_b", "init_boot_b", "dtbo_b", "vendor_boot_b", "vbmeta_b"
    ];

    private string _selectedPartition = "boot";
    private string _customPartition = string.Empty;
    private string _imagePath = string.Empty;
    private bool _disableVerity;
    private bool _autoReboot = true;
    private bool _waitForFastboot;
    private string _status = "选择镜像文件后开始刷入。";
    private string _logText = "";
    private double _percent;
    private string _speedText = "0MB/s | Time:0s";
    private string _blCommand = "fastboot flashing unlock";
    private string _xiaomiDir = string.Empty;
    private string _xiaomiScript = "flash_all.sh";
    private string _extStatus = string.Empty;

    public FlashPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        BrowseCommand = new AsyncCommand(BrowseAsync);
        FlashCommand = new AsyncCommand(() => RunAsync(FlashAsync));
        EraseCommand = new AsyncCommand(() => RunAsync(EraseAsync));
        RunBlCommand = new AsyncCommand(() => RunAsync(RunBlAsync));
        PickXiaomiDirCommand = new AsyncCommand(PickXiaomiDirAsync);
        RunXiaomiCommand = new AsyncCommand(() => RunAsync(RunXiaomiAsync));
        FormatDeviceCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var fb = await RequireFastbootAsync().ConfigureAwait(true);
            AppendLog("erase metadata");
            await _fastboot.EraseAsync(fb, "metadata").ConfigureAwait(true);
            AppendLog("erase userdata");
            await _fastboot.EraseAsync(fb, "userdata").ConfigureAwait(true);
            await _fastboot.RebootAsync(fb).ConfigureAwait(true);
            ExtStatus = "✓ 设备已格式化（双清）";
        }));
        WipeFrpCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var fb = await RequireFastbootAsync().ConfigureAwait(true);
            AppendLog("erase frp");
            await _fastboot.EraseAsync(fb, "frp").ConfigureAwait(true);
            ExtStatus = "✓ 谷歌锁已擦除";
        }));
        FixAdbCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var r = await _toolkit.FixAdbAsync().ConfigureAwait(true);
            ExtStatus = r.Success ? "✓ adb 服务已重启" : $"✗ {r.Combined}";
        }));
        MiUsbCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var serial = await RequireAdbAsync().ConfigureAwait(true);
            // Upstream EnableMiuiUsbButton command chain.
            foreach (var cmd in new[]
                     {
                         "setprop persist.security.adbinput 1",
                         "setprop persist.fastboot.enable 1",
                     })
            {
                await _adb.ShellAsync(serial, cmd).ConfigureAwait(true);
                AppendLog(cmd);
            }
            ExtStatus = "小米USB安全设置已强开";
        }));
        ReadDdrCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var serial = await RequireAdbAsync().ConfigureAwait(true);
            var ddr = await _toolkit.ReadDdrTypeAsync(serial).ConfigureAwait(true);
            ExtStatus = $"检测结果为：{ddr}";
        }));
        DiagCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var serial = await RequireAdbAsync().ConfigureAwait(true);
            var ok = await _toolkit.EnableDiagPortAsync(serial, m => AppendLog(m)).ConfigureAwait(true);
            ExtStatus = ok ? "基带调试端口已开启" : "开启失败（需 root）";
        }));
        MtpCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var serial = await RequireAdbAsync().ConfigureAwait(true);
            await _toolkit.SwitchToMtpAsync(serial).ConfigureAwait(true);
            ExtStatus = "已切换到文件传输 (MTP)";
        }));
        OcdtCommand = new AsyncCommand(() => RunExtAsync(async () =>
        {
            var file = await _pickers.PickOpenFileAsync("选择 OCDT 文件", "xml", "bin").ConfigureAwait(true);
            if (file is null) { ExtStatus = "未选择文件"; return; }
            var dir = Path.Combine(Path.GetDirectoryName(file)!, "ocdt_out");
            var r = await _toolkit.AnalyzeOcdtAsync(file, dir, m => AppendLog(m)).ConfigureAwait(true);
            ExtStatus = r.Success ? $"✓ OCDT 分析完成：{dir}" : $"✗ {r.Combined}";
        }));
    }

    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand FlashCommand { get; }
    public AsyncCommand EraseCommand { get; }
    public AsyncCommand RunBlCommand { get; }
    public AsyncCommand PickXiaomiDirCommand { get; }
    public AsyncCommand RunXiaomiCommand { get; }
    public AsyncCommand FormatDeviceCommand { get; }
    public AsyncCommand WipeFrpCommand { get; }
    public AsyncCommand FixAdbCommand { get; }
    public AsyncCommand MiUsbCommand { get; }
    public AsyncCommand ReadDdrCommand { get; }
    public AsyncCommand DiagCommand { get; }
    public AsyncCommand MtpCommand { get; }
    public AsyncCommand OcdtCommand { get; }

    public ObservableCollection<string> PartitionOptions { get; } = new(CommonPartitions);
    public ObservableCollection<string> XiaomiScripts { get; } = new(XiaomiFlashService.ScriptNames);

    public string SelectedPartition { get => _selectedPartition; set => SetProperty(ref _selectedPartition, value); }
    public string CustomPartition { get => _customPartition; set => SetProperty(ref _customPartition, value); }
    public string ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }
    public bool DisableVerity { get => _disableVerity; set => SetProperty(ref _disableVerity, value); }
    public bool AutoReboot { get => _autoReboot; set => SetProperty(ref _autoReboot, value); }
    public bool WaitForFastboot { get => _waitForFastboot; set => SetProperty(ref _waitForFastboot, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Log { get => _logText; set => SetProperty(ref _logText, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string SpeedText { get => _speedText; set => SetProperty(ref _speedText, value); }
    public string BlCommand { get => _blCommand; set => SetProperty(ref _blCommand, value); }
    public string XiaomiDir { get => _xiaomiDir; set => SetProperty(ref _xiaomiDir, value); }
    public string XiaomiScript { get => _xiaomiScript; set => SetProperty(ref _xiaomiScript, value); }
    public string ExtStatus { get => _extStatus; set => SetProperty(ref _extStatus, value); }

    private string EffectivePartition =>
        string.IsNullOrWhiteSpace(CustomPartition) ? SelectedPartition : CustomPartition.Trim();

    private void AppendLog(string line)
    {
        Log += $"{DateTime.Now:HH:mm:ss}  {line}\n";
        _log(line);
    }

    private async Task<string> RequireAdbAsync()
    {
        var device = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true);
        return device?.Serial ?? throw new InvalidOperationException("未检测到 ADB 设备");
    }

    private async Task<string> RequireFastbootAsync()
        => (await _fastboot.DevicesAsync().ConfigureAwait(true)).FirstOrDefault()?.Serial
           ?? throw new InvalidOperationException("未检测到 Fastboot 设备");

    private async Task BrowseAsync()
    {
        var file = await _pickers.PickOpenFileAsync("选择要刷入的镜像", "img", "bin").ConfigureAwait(true);
        if (file is not null) ImagePath = file;
    }

    private async Task PickXiaomiDirAsync()
    {
        var f = await _pickers.PickOpenFileAsync("选择小米线刷包（.tgz/.zip）", "tgz", "gz", "zip").ConfigureAwait(true);
        if (f is null) return;
        try
        {
            var outDir = Path.Combine(Path.GetDirectoryName(f)!, "_vtb_xiaomi_extract");
            XiaomiDir = await _xiaomi.ExtractPackageAsync(f, outDir, m => AppendLog(m)).ConfigureAwait(true);
            Status = $"小米包已解包：{XiaomiDir}";
        }
        catch (Exception ex) { Status = $"解包失败：{ex.Message}"; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; AppendLog($"[刷入] {ex}"); }
    }

    private async Task RunExtAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { ExtStatus = $"出错：{ex.Message}"; AppendLog($"[扩展] {ex}"); }
    }

    private async Task FlashAsync()
    {
        var partition = EffectivePartition;
        if (!File.Exists(ImagePath))
        {
            Status = "请先选择有效的镜像文件。";
            return;
        }

        var fb = await _fastboot.DevicesAsync().ConfigureAwait(true);
        if (fb.Count > 0)
        {
            Status = $"正在通过 fastboot 刷入 {partition}…";
            AppendLog($"fastboot flash {partition} {ImagePath}");
            // Upstream: vbmeta + DisableDmVerity checkbox → --disable-verity --disable-verification.
            var flags = DisableVerity && partition.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase)
                ? "--disable-verity --disable-verification " : "";
            var psi = ProcessRunner.MakeStartInfo(_fastboot.FastbootPath!,
                $"-s {fb[0].Serial} {flags}flash {partition} \"{ImagePath}\"");
            var result = await ProcessRunner.RunAsync(psi, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(true);
            Status = result.Success ? $"✓ {partition} 刷入完成。" : $"✗ 刷入失败：{result.Combined}";
            AppendLog(result.Combined.Trim());
            if (result.Success && AutoReboot)
            {
                await _fastboot.RebootAsync(fb[0].Serial).ConfigureAwait(true);
                AppendLog("[Rebooting] 重启设备.");
            }
            return;
        }

        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true);
        if (adb is not null)
        {
            Status = $"正在通过 adb(root)+dd 写入 {partition}…";
            await _partitions.WriteAdbPartitionAsync(adb.Serial, partition, ImagePath, null).ConfigureAwait(true);
            Status = $"✓ {partition} 写入完成。";
            return;
        }

        Status = "未检测到 Fastboot 或 ADB(root) 设备。";
    }

    private async Task EraseAsync()
    {
        var partition = EffectivePartition;
        var fb = await RequireFastbootAsync().ConfigureAwait(true);
        Status = $"正在擦除 {partition}…";
        AppendLog($"fastboot erase {partition}");
        var result = await _fastboot.EraseAsync(fb, partition).ConfigureAwait(true);
        Status = result.Success ? $"✓ {partition} 已擦除。" : $"✗ 擦除失败：{result.Combined}";
    }

    private async Task RunBlAsync()
    {
        var args = BlCommand.StartsWith("fastboot ") ? BlCommand["fastboot ".Length..] : BlCommand;
        var fb = await RequireFastbootAsync().ConfigureAwait(true);
        Status = $"执行 {BlCommand} …（高通设备请按两下音量下并确认）";
        AppendLog($"fastboot {args}");
        var result = await _fastboot.RunAsync(fb, args, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(true);
        Status = result.Success ? "✓ 指令已执行。" : $"✗ {result.Combined}";
    }

    private async Task RunXiaomiAsync()
    {
        if (!XiaomiFlashService.LooksLikeXiaomiPackage(XiaomiDir))
            throw new InvalidOperationException("请先选择并解包小米线刷包。");
        Status = "小米官方脚本刷写中…";
        var result = await _xiaomi.RunScriptAsync(XiaomiDir, XiaomiScript, m => AppendLog($"[线刷] {m}")).ConfigureAwait(true);
        Status = result.Success ? "✓ 小米线刷完成。" : $"✗ 退出码 {result.ExitCode}，详见日志。";
    }
}
