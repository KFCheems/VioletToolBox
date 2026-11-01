using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VioletToolBox.Core.Download;

namespace VioletToolBox.Linux.ViewModels;

public sealed class DownloadPageViewModel : ViewModelBase
{
    private readonly Action<string> _log;

    private string _url = string.Empty;
    private string _saveDir = string.Empty;
    private string _fileName = string.Empty;
    private int _splitCount = 16;
    private double _percent;
    private string _speed = string.Empty;
    private string _status = "多连接下载基于 aria2c（sudo dnf install aria2）。";

    private CancellationTokenSource? _cts;

    public DownloadPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        PickDirCommand = new AsyncCommand(async () =>
        {
            var d = await pickers.PickFolderAsync("选择保存目录").ConfigureAwait(true);
            if (d is not null) SaveDir = d;
        });
        StartCommand = new AsyncCommand(() => RunAsync(StartAsync));
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public AsyncCommand PickDirCommand { get; }
    public AsyncCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string Url { get => _url; set => SetProperty(ref _url, value); }
    public string SaveDir { get => _saveDir; set => SetProperty(ref _saveDir, value); }
    public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
    public int SplitCount { get => _splitCount; set => SetProperty(ref _splitCount, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidOperationException("请输入下载链接。");
        if (string.IsNullOrWhiteSpace(SaveDir))
            throw new InvalidOperationException("请选择保存目录。");

        var name = string.IsNullOrWhiteSpace(FileName) ? Path.GetFileName(new Uri(Url).LocalPath) : FileName.Trim();
        if (string.IsNullOrEmpty(name)) name = "rom_download.bin";
        var outputPath = Path.Combine(SaveDir, name);

        _cts = new CancellationTokenSource();
        Status = "下载中…";
        _log($"[下载] {Url} → {outputPath}");

        var result = await Aria2DownloadService.DownloadAsync(Url, outputPath, SplitCount,
            p =>
            {
                Percent = p.Percentage;
                Speed = $"{p.SpeedText}  连接数 {p.ConnectionCount}  ETA {p.EtaText}";
                Status = $"下载中… {p.Percentage:F1}%";
            },
            _cts.Token).ConfigureAwait(true);

        if (result.Succeeded)
        {
            Percent = 100;
            Status = $"✓ 下载完成：{outputPath}";
            _log($"[下载] 完成：{outputPath}");
        }
        else if (_cts.IsCancellationRequested)
        {
            Status = "已取消。";
        }
        else
        {
            Status = $"✗ 下载失败：{result.ErrorMessage}";
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消。";
        }
        catch (Exception ex)
        {
            Status = $"出错：{ex.Message}";
            _log($"[下载] {ex}");
        }
    }
}
