using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VioletToolBox.Core.Flash;

namespace VioletToolBox.Linux.ViewModels;

public sealed class OujiaPageViewModel : ViewModelBase
{
    private readonly OujiaFlashService _engine = new();
    private readonly XiaomiFlashService _xiaomi = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;
    private CancellationTokenSource? _cts;

    private string _packageDir = string.Empty;
    private OujiaMode _mode = OujiaMode.NormalFull;
    private string _arbInfo = string.Empty;
    private double _percent;
    private string _status = "选择解包后的全量包目录（或售后包 XML 脚本）。";
    private string _xiaomiDir = string.Empty;
    private string _xiaomiScript = "flash_all.sh";

    public OujiaPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        PickDirCommand = new AsyncCommand(async () =>
        {
            var d = await _pickers.PickFolderAsync("选择全量包目录").ConfigureAwait(true);
            if (d is not null) PackageDir = d;
        });
        CheckArbCommand = new AsyncCommand(() => RunAsync(CheckArbAsync));
        StartCommand = new AsyncCommand(() => RunAsync(StartAsync));
        StopCommand = new RelayCommand(() => _cts?.Cancel());
        PickXiaomiDirCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择小米线刷包（.tgz/.zip）", "tgz", "gz", "zip").ConfigureAwait(true);
            if (f is not null) _ = ExtractXiaomiAsync(f);
        });
        RunXiaomiCommand = new AsyncCommand(() => RunAsync(RunXiaomiAsync));
    }

    public AsyncCommand PickDirCommand { get; }
    public AsyncCommand CheckArbCommand { get; }
    public AsyncCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncCommand PickXiaomiDirCommand { get; }
    public AsyncCommand RunXiaomiCommand { get; }

    public static readonly (string Label, OujiaMode Mode)[] ModeTable =
    [
        ("普通线刷", OujiaMode.NormalFull),
        ("强力线刷（擦数据）", OujiaMode.StrongFull),
        ("AB 通刷", OujiaMode.AbFull),
        ("仅 FastbootD", OujiaMode.FastbootDOnly),
        ("修复 FastbootD", OujiaMode.RepairFastbootD),
        ("售后 FB 模式", OujiaMode.SvcFb),
        ("售后 FBD 模式", OujiaMode.SvcFbd),
    ];

    public ObservableCollection<string> Modes { get; } = new(ModeTable.Select(m => m.Label));
    public ObservableCollection<string> XiaomiScripts { get; } = new(XiaomiFlashService.ScriptNames);

    public string PackageDir { get => _packageDir; set => SetProperty(ref _packageDir, value); }

    private string _modeName = "普通线刷";

    public string ModeName
    {
        get => _modeName;
        set
        {
            if (SetProperty(ref _modeName, value))
                _mode = ModeTable.FirstOrDefault(m => m.Label == value).Mode;
        }
    }

    public string ArbInfo { get => _arbInfo; set => SetProperty(ref _arbInfo, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string XiaomiDir { get => _xiaomiDir; set => SetProperty(ref _xiaomiDir, value); }
    public string XiaomiScript { get => _xiaomiScript; set => SetProperty(ref _xiaomiScript, value); }

    private async Task ExtractXiaomiAsync(string archive)
    {
        try
        {
            var outDir = Path.Combine(Path.GetDirectoryName(archive)!, "_vtb_xiaomi_extract");
            XiaomiDir = await _xiaomi.ExtractPackageAsync(archive, outDir, m => _log(m)).ConfigureAwait(true);
            Status = $"✓ 小米包已解包：{XiaomiDir}";
        }
        catch (Exception ex)
        {
            Status = $"解包失败：{ex.Message}";
        }
    }

    private async Task CheckArbAsync()
    {
        var fb = await new Core.Android.FastbootService().FirstDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 Fastboot 设备。");
        ArbInfo = await _engine.CheckArbAsync(fb.Serial).ConfigureAwait(true);
        _log($"[欧加] ARB：{ArbInfo}");
    }

    private async Task StartAsync()
    {
        if (!Directory.Exists(PackageDir))
            throw new InvalidOperationException("请选择有效的固件目录。");
        var fb = await new Core.Android.FastbootService().FirstDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 Fastboot 设备。");
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p => Percent = p);
        Status = $"线刷中（{_modeName}）…";
        await _engine.RunAsync(fb.Serial, PackageDir, _mode, m => _log(m), progress, _cts.Token).ConfigureAwait(true);
        Percent = 100;
        Status = "✓ 完成。";
    }

    private async Task RunXiaomiAsync()
    {
        if (!XiaomiFlashService.LooksLikeXiaomiPackage(XiaomiDir))
            throw new InvalidOperationException("请先选择并解包小米线刷包。");
        _cts = new CancellationTokenSource();
        Status = "小米官方脚本刷写中…";
        var result = await _xiaomi.RunScriptAsync(XiaomiDir, XiaomiScript, m => _log(m), _cts.Token).ConfigureAwait(true);
        Status = result.Success ? "✓ 小米线刷完成。" : $"✗ 退出码 {result.ExitCode}，详见日志。";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (OperationCanceledException) { Status = "已取消。"; }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[欧加] {ex}"); }
    }
}
