using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core.Android;
using VioletToolBox.Core.Root;

namespace VioletToolBox.Linux.ViewModels;

public sealed class ModulePageViewModel : ViewModelBase
{
    private readonly ModuleService _modules = new();
    private readonly AdbService _adb = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private string _selectedManager = "KernelSU";
    private string _moduleZip = string.Empty;
    private string _status = "快捷安装会从 GitHub 下载最新管理器 APK 并通过 adb 安装；模块安装需要设备 root。";

    public ModulePageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        InstallManagerCommand = new AsyncCommand(InstallManagerAsync);
        PickModuleCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择模块 zip", "zip").ConfigureAwait(true);
            if (f is not null) ModuleZip = f;
        });
        InstallModuleCommand = new AsyncCommand(() => RunAsync(InstallModuleAsync));
        HideRootCommand = new AsyncCommand(() => RunAsync(HideRootAsync));
    }

    public AsyncCommand InstallManagerCommand { get; }
    public AsyncCommand PickModuleCommand { get; }
    public AsyncCommand InstallModuleCommand { get; }
    public AsyncCommand HideRootCommand { get; }

    public ObservableCollection<string> Managers { get; } = new(["Magisk", "KernelSU", "SukiSU", "APatch"]);

    public string SelectedManager { get => _selectedManager; set => SetProperty(ref _selectedManager, value); }
    public string ModuleZip { get => _moduleZip; set => SetProperty(ref _moduleZip, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task InstallManagerAsync()
    {
        Status = $"正在获取 {SelectedManager} 最新版…";
        var url = await _modules.ResolveManagerApkUrlAsync(SelectedManager).ConfigureAwait(true)
            ?? throw new InvalidOperationException("未能从 GitHub releases 解析 APK（检查网络）。");
        _log($"[模块] 下载 {url}");

        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "violettoolbox");
        Directory.CreateDirectory(cache);
        var apkPath = Path.Combine(cache, $"{SelectedManager}_{DateTime.Now:yyyyMMddHHmm}.apk");
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VioletToolBox-Linux");
        await using (var fs = File.Create(apkPath))
        await using (var stream = await http.GetStreamAsync(url).ConfigureAwait(true))
        {
            await stream.CopyToAsync(fs).ConfigureAwait(true);
        }
        _log($"[模块] 已下载 {apkPath}，adb install…");

        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");
        var result = await new AppManagementService().InstallApkAsync(adb.Serial, apkPath).ConfigureAwait(true);
        Status = result.Success ? $"✓ {SelectedManager} 安装成功。" : $"✗ 安装失败：{result.Combined}";
        _log($"[模块] {result.Combined}");
    }

    private async Task InstallModuleAsync()
    {
        if (!File.Exists(ModuleZip))
            throw new InvalidOperationException("请选择模块 zip 文件。");
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备（需 root）。");
        Status = "安装模块中…";
        var output = await _modules.InstallModuleZipAsync(adb.Serial, ModuleZip, m => _log(m)).ConfigureAwait(true);
        Status = "✓ 模块安装指令已执行（详见日志）。";
    }

    private async Task HideRootAsync()
    {
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备（需 root）。");
        Status = "执行隐藏 ROOT…";
        var results = await _modules.HideRootAsync(adb.Serial).ConfigureAwait(true);
        foreach (var r in results) _log($"[隐藏] {r}");
        Status = "✓ 隐藏 ROOT 指令已执行。";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[模块] {ex}"); }
    }
}
