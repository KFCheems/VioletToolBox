using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VioletToolBox.Core.Root;

namespace VioletToolBox.Linux.ViewModels;

public sealed class BootPatchPageViewModel : ViewModelBase
{
    private readonly BootPatchService _patcher = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private string _bootImg = string.Empty;
    private string _superKey = string.Empty;
    private string _outputDir = string.Empty;
    private string _lkmPath = string.Empty;
    private double _percent;
    private string _status = "支持 magiskboot 重打包与 APatch（kptools+kpimg）内核修补，输出到指定目录。";

    public BootPatchPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        PickBootCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择 boot.img", "img").ConfigureAwait(true);
            if (f is not null) BootImg = f;
        });
        PickOutputCommand = new AsyncCommand(async () =>
        {
            var d = await _pickers.PickFolderAsync("选择输出目录").ConfigureAwait(true);
            if (d is not null) OutputDir = d;
        });
        PickLkmCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择 LKM (.ko)", "ko").ConfigureAwait(true);
            if (f is not null) LkmPath = f;
        });
        InspectCommand = new AsyncCommand(() => RunAsync(InspectAsync));
        PatchMagiskCommand = new AsyncCommand(() => RunAsync(PatchMagiskAsync));
        PatchApatchCommand = new AsyncCommand(() => RunAsync(PatchApatchAsync));
        InstallLkmCommand = new AsyncCommand(() => RunAsync(InstallLkmAsync));
    }

    public AsyncCommand PickBootCommand { get; }
    public AsyncCommand PickOutputCommand { get; }
    public AsyncCommand PickLkmCommand { get; }
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand PatchMagiskCommand { get; }
    public AsyncCommand PatchApatchCommand { get; }
    public AsyncCommand InstallLkmCommand { get; }

    public string BootImg { get => _bootImg; set => SetProperty(ref _bootImg, value); }
    public string SuperKey { get => _superKey; set => SetProperty(ref _superKey, value); }
    public string OutputDir { get => _outputDir; set => SetProperty(ref _outputDir, value); }
    public string LkmPath { get => _lkmPath; set => SetProperty(ref _lkmPath, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string RequireOutput()
    {
        if (string.IsNullOrWhiteSpace(OutputDir))
            throw new InvalidOperationException("请先选择输出目录。");
        Directory.CreateDirectory(OutputDir);
        return OutputDir;
    }

    private async Task InspectAsync()
    {
        if (!File.Exists(BootImg)) throw new InvalidOperationException("请选择 boot.img。");
        var work = Path.Combine(RequireOutput(), "_boot_work");
        Status = "解析 boot 镜像…";
        var info = await _patcher.InspectAsync(BootImg, work).ConfigureAwait(true);
        Status = "✓ 解包完成（kernel/ramdisk 在工作目录）。";
        _log($"[修补] {info}");
    }

    private async Task PatchMagiskAsync()
    {
        if (!File.Exists(BootImg)) throw new InvalidOperationException("请选择 boot.img。");
        var work = Path.Combine(RequireOutput(), "_boot_work");
        Status = "magiskboot 重打包…";
        var r = await _patcher.PatchWithMagiskAsync(BootImg, work, m => _log(m)).ConfigureAwait(true);
        Status = $"✓ 已生成 {r.OutputPath}";
        Percent = 100;
    }

    private async Task PatchApatchAsync()
    {
        if (!File.Exists(BootImg)) throw new InvalidOperationException("请选择 boot.img。");
        if (string.IsNullOrWhiteSpace(SuperKey))
            throw new InvalidOperationException("APatch 修补需要设置超级密钥（SuperKey）。");
        Status = "kptools 修补内核…";
        var r = await _patcher.PatchWithApatchAsync(BootImg, SuperKey.Trim(), m => _log(m)).ConfigureAwait(true);
        Status = $"✓ 已生成 {r.OutputPath}";
        Percent = 100;
    }

    private async Task InstallLkmAsync()
    {
        if (!File.Exists(LkmPath)) throw new InvalidOperationException("请选择 LKM .ko 文件。");
        var adb = await new Core.Android.AdbService().FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备（需 root）。");
        Status = "推送并 insmod LKM…";
        await _patcher.InstallLkmOnDeviceAsync(adb.Serial, LkmPath, m => _log(m)).ConfigureAwait(true);
        Status = "✓ LKM 已加载。";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[修补] {ex}"); }
    }
}
