using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Linux.ViewModels;

public sealed class PartitionRowViewModel
{
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed class PartitionPageViewModel : ViewModelBase
{
    private readonly AdbService _adb = new();
    private readonly PartitionService _partitions = new();
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private PartitionRowViewModel? _selected;
    private string _mode = "adb";
    private double _percent;
    private string _progressText = string.Empty;

    public PartitionPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ReadCommand = new AsyncCommand(() => RunAsync(ReadAsync));
        WriteCommand = new AsyncCommand(() => RunAsync(WriteAsync));
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand WriteCommand { get; }

    public ObservableCollection<PartitionRowViewModel> Partitions { get; } = [];

    public PartitionRowViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>"adb" (root via dd) or "fastboot".</summary>
    public string Mode
    {
        get => _mode;
        set { if (SetProperty(ref _mode, value)) { Raise(nameof(IsAdbMode)); Raise(nameof(IsFastbootMode)); } }
    }

    public bool IsAdbMode
    {
        get => Mode == "adb";
        set => Mode = value ? "adb" : "fastboot";
    }

    public bool IsFastbootMode
    {
        get => Mode != "adb";
        set => Mode = value ? "fastboot" : "adb";
    }

    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    private async Task RefreshAsync()
    {
        Partitions.Clear();
        try
        {
            if (Mode == "fastboot")
            {
                var fb = await new FastbootService().FirstDeviceAsync().ConfigureAwait(true)
                    ?? throw new InvalidOperationException("未检测到 Fastboot 设备。");
                var list = await _partitions.ListFastbootPartitionsAsync(fb.Serial).ConfigureAwait(true);
                Fill(list);
            }
            else
            {
                var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
                    ?? throw new InvalidOperationException("未检测到 ADB 在线设备。");
                var list = await _partitions.ListAdbPartitionsAsync(adb.Serial).ConfigureAwait(true);
                Fill(list);
            }
            ProgressText = $"共 {Partitions.Count} 个分区。";
        }
        catch (Exception ex)
        {
            ProgressText = $"读取分区表失败：{ex.Message}";
            _log($"[分区] {ex.Message}");
        }
    }

    private void Fill(System.Collections.Generic.IEnumerable<PartitionEntry> entries)
    {
        foreach (var e in entries)
            Partitions.Add(new PartitionRowViewModel
            {
                Name = e.Name,
                Size = e.SizeDisplay,
                Source = e.Source,
            });
    }

    private async Task ReadAsync()
    {
        if (Selected is null) { ProgressText = "请先选择分区。"; return; }
        var savePath = await _pickers.PickSaveFileAsync($"保存分区 {Selected.Name}", $"{Selected.Name}.img").ConfigureAwait(true);
        if (savePath is null) return;

        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
            ?? throw new InvalidOperationException("读取分区需要 ADB 在线设备（root）。");
        var progress = new Progress<PartitionProgress>(p =>
        {
            Percent = p.Percent ?? 0;
            ProgressText = $"{p.Message}";
        });
        _log($"[分区] 读取 {Selected.Name} → {savePath}");
        await _partitions.ReadAdbPartitionAsync(adb.Serial, Selected.Name, savePath, progress).ConfigureAwait(true);
        Percent = 100;
        ProgressText = $"✓ 已保存到 {savePath}";
        _log($"[分区] 读取完成：{Selected.Name}");
    }

    private async Task WriteAsync()
    {
        if (Selected is null) { ProgressText = "请先选择分区。"; return; }
        var file = await _pickers.PickOpenFileAsync($"选择写入 {Selected.Name} 的镜像", "img", "bin").ConfigureAwait(true);
        if (file is null) return;

        if (Mode == "fastboot")
        {
            var fb = await new FastbootService().FirstDeviceAsync().ConfigureAwait(true)
                ?? throw new InvalidOperationException("未检测到 Fastboot 设备。");
            var progress = new Progress<PartitionProgress>(p =>
            {
                Percent = p.Percent ?? 0;
                ProgressText = p.Message;
            });
            _log($"[分区] fastboot flash {Selected.Name} {file}");
            await _partitions.WriteFastbootPartitionAsync(fb.Serial, Selected.Name, file, progress).ConfigureAwait(true);
        }
        else
        {
            var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true)
                ?? throw new InvalidOperationException("写入分区需要 ADB 在线设备（root）。");
            var progress = new Progress<PartitionProgress>(p =>
            {
                Percent = p.Percent ?? 0;
                ProgressText = p.Message;
            });
            _log($"[分区] dd 写入 {Selected.Name} ← {file}");
            await _partitions.WriteAdbPartitionAsync(adb.Serial, Selected.Name, file, progress).ConfigureAwait(true);
        }
        Percent = 100;
        ProgressText = $"✓ {Selected.Name} 写入完成。";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ProgressText = $"出错：{ex.Message}";
            _log($"[分区] {ex}");
        }
    }
}
