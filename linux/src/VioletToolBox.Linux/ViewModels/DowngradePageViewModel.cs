using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core;
using VioletToolBox.Core.Downgrade;

namespace VioletToolBox.Linux.ViewModels;

/// <summary>降级助手 — drives the ported DowngradeTool engine (OPPO/OnePlus OTA 降级).</summary>
public sealed class DowngradePageViewModel : ViewModelBase
{
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;
    private DowngradeTool? _tool;

    private string _assistantApk = string.Empty;
    private string _downloadDir = string.Empty;
    private bool _queryOnly = true;
    private double _percent;
    private string _status = "安装 ColorOS 助手 APK（可选）→ 仅查询降级包版本 → 下载并推送降级。";

    public DowngradePageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        PickApkCommand = new AsyncCommand(async () =>
        {
            var f = await _pickers.PickOpenFileAsync("选择 ColorOS 助手 APK", "apk").ConfigureAwait(true);
            if (f is not null) AssistantApk = f;
        });
        PickDirCommand = new AsyncCommand(async () =>
        {
            var d = await _pickers.PickFolderAsync("选择下载目录").ConfigureAwait(true);
            if (d is not null) DownloadDir = d;
        });
        RunCommand = new AsyncCommand(RunAsync);
    }

    public AsyncCommand PickApkCommand { get; }
    public AsyncCommand PickDirCommand { get; }
    public AsyncCommand RunCommand { get; }

    public ObservableCollection<DowngradeTool.DowngradePackageInfo> Packages { get; } = [];

    public string AssistantApk { get => _assistantApk; set => SetProperty(ref _assistantApk, value); }
    public string DownloadDir { get => _downloadDir; set => SetProperty(ref _downloadDir, value); }
    public bool QueryOnly { get => _queryOnly; set => SetProperty(ref _queryOnly, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task RunAsync()
    {
        if (ToolLocator.Find("adb") is null)
            throw new InvalidOperationException("未找到 adb。");
        _downloadDir = string.IsNullOrWhiteSpace(DownloadDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : DownloadDir;

        var tool = new DowngradeTool(m => { _log(m); Status = m.Length > 60 ? m[..60] + "…" : m; });
        tool.OnPackagesFound += list =>
        {
            Packages.Clear();
            foreach (var p in list.Take(50)) Packages.Add(p);
        };
        tool.OnDownloadProgress += (pct, speed, text) =>
        {
            Percent = pct;
            Status = $"{text} {pct:F1}%  {speed:F1}MB/s";
        };
        _tool = tool;

        Status = "降级工具运行中…";
        await Task.Run(() => tool.RunAsync(new DowngradeTool.DowngradeOptions
        {
            DownloadDir = DownloadDir,
            QueryOnly = QueryOnly,
            ApkPath = AssistantApk,
            InstallApkBeforeQuery = File.Exists(AssistantApk),
        })).ConfigureAwait(true);
        Status = "✓ 降级工具流程结束（详见日志）。";
    }
}
