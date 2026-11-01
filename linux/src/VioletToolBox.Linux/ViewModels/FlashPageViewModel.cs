using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Linux.ViewModels;

public sealed class FlashPageViewModel : ViewModelBase
{
    private readonly AdbService _adb = new();
    private readonly FastbootService _fastboot = new();
    private readonly PartitionService _partitions = new();
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
    private string _status = "选择镜像文件后开始刷入。";

    public FlashPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        BrowseCommand = new AsyncCommand(BrowseAsync);
        FlashCommand = new AsyncCommand(() => RunAsync(FlashAsync));
        EraseCommand = new AsyncCommand(() => RunAsync(EraseAsync));
    }

    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand FlashCommand { get; }
    public AsyncCommand EraseCommand { get; }

    public ObservableCollection<string> PartitionOptions { get; } = new(CommonPartitions);

    public string SelectedPartition
    {
        get => _selectedPartition;
        set => SetProperty(ref _selectedPartition, value);
    }

    public string CustomPartition
    {
        get => _customPartition;
        set => SetProperty(ref _customPartition, value);
    }

    public string ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }
    public bool DisableVerity { get => _disableVerity; set => SetProperty(ref _disableVerity, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string EffectivePartition =>
        string.IsNullOrWhiteSpace(CustomPartition) ? SelectedPartition : CustomPartition.Trim();

    private async Task BrowseAsync()
    {
        var file = await _pickers.PickOpenFileAsync("选择要刷入的镜像", "img", "bin").ConfigureAwait(true);
        if (file is not null) ImagePath = file;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"出错：{ex.Message}";
            _log($"[刷入] {ex}");
        }
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
            _log($"[刷入] fastboot flash {partition} {ImagePath}");
            var flags = DisableVerity ? "--disable-verity --disable-verification " : "";
            var psi = ProcessRunner.MakeStartInfo(_fastboot.FastbootPath!,
                $"-s {fb[0].Serial} {flags}flash {partition} \"{ImagePath}\"");
            var result = await ProcessRunner.RunAsync(psi, timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(true);
            Status = result.Success ? $"✓ {partition} 刷入完成。" : $"✗ 刷入失败：{result.Combined}";
            _log($"[刷入] {result.Combined}");
            return;
        }

        var adb = await _adb.FirstOnlineDeviceAsync().ConfigureAwait(true);
        if (adb is not null)
        {
            Status = $"正在通过 adb(root)+dd 写入 {partition}…";
            var progress = new Progress<PartitionProgress>(p => Status = $"{p.Message} {p.Percent:F1}%");
            await _partitions.WriteAdbPartitionAsync(adb.Serial, partition, ImagePath, progress).ConfigureAwait(true);
            Status = $"✓ {partition} 写入完成。";
            return;
        }

        Status = "未检测到 Fastboot 或 ADB(root) 设备。";
    }

    private async Task EraseAsync()
    {
        var partition = EffectivePartition;
        var fb = await _fastboot.DevicesAsync().ConfigureAwait(true);
        if (fb.Count == 0)
        {
            Status = "擦除分区需要 Fastboot 模式（避免误擦，不支持 ADB 路径）。";
            return;
        }
        Status = $"正在擦除 {partition}…";
        var result = await _fastboot.EraseAsync(fb[0].Serial, partition).ConfigureAwait(true);
        Status = result.Success ? $"✓ {partition} 已擦除。" : $"✗ 擦除失败：{result.Combined}";
        _log($"[刷入] erase {partition}: {result.Combined}");
    }
}
