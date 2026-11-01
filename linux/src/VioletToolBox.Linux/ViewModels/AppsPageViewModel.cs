using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core.Android;
using VioletToolBox.Core.Backup;

namespace VioletToolBox.Linux.ViewModels;

public sealed record BackupFileRow(string Name, string Size);

public sealed class BackupPageViewModel : ViewModelBase
{
    private readonly BackupService _backup = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private string _mode = "photos";
    private string _saveDir = string.Empty;
    private double _percent;
    private string _status = "图片/视频按目录 adb pull；通讯录走 content provider 导出 VCF。";

    public BackupPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        PickDirCommand = new AsyncCommand(async () =>
        {
            var d = await _pickers.PickFolderAsync("选择备份保存目录").ConfigureAwait(true);
            if (d is not null) SaveDir = d;
        });
        BackupCommand = new AsyncCommand(() => RunAsync(BackupAsync));
    }

    public AsyncCommand PickDirCommand { get; }
    public AsyncCommand BackupCommand { get; }

    public string Mode
    {
        get => _mode;
        set { if (SetProperty(ref _mode, value)) { Raise(nameof(IsPhotos)); Raise(nameof(IsContacts)); Raise(nameof(IsVideos)); } }
    }
    public bool IsPhotos { get => Mode == "photos"; set => Mode = value ? "photos" : Mode; }
    public bool IsVideos { get => Mode == "videos"; set => Mode = value ? "videos" : Mode; }
    public bool IsContacts { get => Mode == "contacts"; set => Mode = value ? "contacts" : Mode; }
    public string SaveDir { get => _saveDir; set => SetProperty(ref _saveDir, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task BackupAsync()
    {
        if (string.IsNullOrWhiteSpace(SaveDir))
            throw new InvalidOperationException("请先选择保存目录。");
        Directory.CreateDirectory(SaveDir);
        var adb = await new AdbService().FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");

        switch (Mode)
        {
            case "photos":
                Status = "备份相机照片…";
                await _backup.PullAllAsync(adb.Serial, "/sdcard/DCIM/Camera", Path.Combine(SaveDir, "DCIM"),
                    new Progress<string>(m => { Status = m; Percent += 10; Percent %= 100; })).ConfigureAwait(true);
                Status = "✓ 照片备份完成。";
                break;
            case "videos":
                Status = "备份视频（可能较大，请耐心等待）…";
                await _backup.PullAllAsync(adb.Serial, "/sdcard/DCIM/Camera", Path.Combine(SaveDir, "Videos")).ConfigureAwait(true);
                Status = "✓ 视频备份完成。";
                break;
            case "contacts":
                Status = "导出通讯录…";
                var vcf = await _backup.BackupContactsAsync(adb.Serial, SaveDir, m => _log(m)).ConfigureAwait(true);
                Status = vcf is null ? "✗ 通讯录导出失败，详见日志。" : $"✓ 已保存 {vcf}";
                break;
        }
        Percent = 100;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[备份] {ex}"); }
    }
}

/// <summary>应用与文件 page.</summary>
public sealed record AppRow(string Package, string Version)
{
    public string Display => string.IsNullOrEmpty(Version) || Version == "—" ? Package : $"{Package}  ({Version})";
}

public sealed class AppsPageViewModel : ViewModelBase
{
    private readonly AppManagementService _apps = new();
    private readonly AdbService _adb = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private AppRow? _selected;
    private string _filter = string.Empty;
    private string _remotePath = "/sdcard/Download/";
    private string _localPath = string.Empty;
    private string _status = "冻结=pm suspend；卸载=user 0（可恢复）。文件传输支持推/拉/删除。";

    public AppsPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        FreezeCommand = new AsyncCommand(() => RunAsync(() => ApplyAsync((a, s) => a.FreezeAsync(s, Selected!.Package))));
        UnfreezeCommand = new AsyncCommand(() => RunAsync(() => ApplyAsync((a, s) => a.UnfreezeAsync(s, Selected!.Package))));
        UninstallCommand = new AsyncCommand(() => RunAsync(() => ApplyAsync((a, s) => a.UninstallAsync(s, Selected!.Package))));
        InstallApkCommand = new AsyncCommand(InstallApkAsync);
        PushCommand = new AsyncCommand(() => RunAsync(() => TransferAsync(push: true)));
        PullCommand = new AsyncCommand(() => RunAsync(() => TransferAsync(push: false)));
        DeleteRemoteCommand = new AsyncCommand(() => RunAsync(DeleteRemoteAsync));
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand FreezeCommand { get; }
    public AsyncCommand UnfreezeCommand { get; }
    public AsyncCommand UninstallCommand { get; }
    public AsyncCommand InstallApkCommand { get; }
    public AsyncCommand PushCommand { get; }
    public AsyncCommand PullCommand { get; }
    public AsyncCommand DeleteRemoteCommand { get; }

    public ObservableCollection<AppRow> Apps { get; } = [];
    public ObservableCollection<string> RemoteFiles { get; } = [];

    public AppRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public string Filter { get => _filter; set => SetProperty(ref _filter, value); }
    public string RemotePath { get => _remotePath; set => SetProperty(ref _remotePath, value); }
    public string LocalPath { get => _localPath; set => SetProperty(ref _localPath, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public string FilteredTip => Apps.Count == 0 ? "点击「刷新」加载应用列表。" : $"共 {Apps.Count} 个应用。";

    private async Task RefreshAsync()
    {
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");
        Apps.Clear();
        Status = "读取应用列表…";
        var list = await _apps.ListThirdPartyAsync(adb.Serial).ConfigureAwait(true);
        foreach (var a in list) Apps.Add(new AppRow(a.Package, a.Version));
        Status = $"✓ 共 {list.Count} 个第三方应用。";
        Raise(nameof(FilteredTip));

        RemoteFiles.Clear();
        var files = await _apps.ListSdcardAsync(adb.Serial, "").ConfigureAwait(true);
        foreach (var f in files) RemoteFiles.Add(f);
    }

    private async Task ApplyAsync(Func<AppManagementService, string, Task> op)
    {
        if (Selected is null) throw new InvalidOperationException("请先选择应用。");
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");
        await op(_apps, adb.Serial).ConfigureAwait(true);
        _log($"[应用] {Selected.Package} 操作完成。");
        Status = $"✓ {Selected.Package} 完成。";
    }

    private async Task InstallApkAsync()
    {
        var apk = await _pickers.PickOpenFileAsync("选择 APK", "apk").ConfigureAwait(true);
        if (apk is null) return;
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");
        Status = $"安装 {Path.GetFileName(apk)}…";
        var r = await _apps.InstallApkAsync(adb.Serial, apk).ConfigureAwait(true);
        Status = r.Success ? "✓ APK 安装成功。" : $"✗ {r.Combined}";
        _log($"[应用] {r.Combined}");
    }

    private async Task TransferAsync(bool push)
    {
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");
        if (push)
        {
            var f = await _pickers.PickOpenFileAsync("选择要推送的文件").ConfigureAwait(true);
            if (f is null) return;
            Status = $"推送 {Path.GetFileName(f)} → {RemotePath}";
            var r = await _apps.PushAsync(adb.Serial, f, RemotePath).ConfigureAwait(true);
            Status = r.Success ? "✓ 推送完成。" : $"✗ {r.Combined}";
        }
        else
        {
            var save = await _pickers.PickSaveFileAsync("保存到", Path.GetFileName(RemotePath)).ConfigureAwait(true);
            if (save is null) return;
            Status = $"拉取 {RemotePath} → {save}";
            var r = await _apps.PullAsync(adb.Serial, RemotePath, save).ConfigureAwait(true);
            Status = r.Success ? "✓ 拉取完成。" : $"✗ {r.Combined}";
        }
        _log($"[文件] {Status}");
    }

    private async Task DeleteRemoteAsync()
    {
        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("未检测到 ADB 设备。");
        await _apps.DeleteRemoteAsync(adb.Serial, RemotePath).ConfigureAwait(true);
        Status = $"✓ 已删除 {RemotePath}";
        _log($"[文件] 删除 {RemotePath}");
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception ex) { Status = $"出错：{ex.Message}"; _log($"[应用] {ex}"); }
    }
}
