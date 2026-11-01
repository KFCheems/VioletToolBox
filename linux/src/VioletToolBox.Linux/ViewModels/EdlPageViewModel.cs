using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core.Edl;

namespace VioletToolBox.Linux.ViewModels;

public sealed class EdlPartitionRowViewModel
{
    public string Label { get; init; } = "";
    public string Size { get; init; } = "";
    public int Lun { get; init; }
    public required EdlPartition Partition { get; init; }
}

public sealed class EdlPageViewModel : ViewModelBase
{
    private readonly Action<string> _log;
    private readonly IFilePickers _pickers;

    private EdlSession? _session;

    private string? _selectedPort;
    private string _programmerPath = string.Empty;
    private string _deviceInfo = "EDL（9008）模式基于 SharpEDL 纯托管实现，无需 fh_loader.exe。\n请选择串口与 firehose programmer（.mbn/.elf）后连接。";
    private EdlPartitionRowViewModel? _selectedPartition;
    private double _percent;
    private string _status = string.Empty;

    public EdlPageViewModel(Action<string> log, IFilePickers pickers)
    {
        _log = log;
        _pickers = pickers;
        RefreshPortsCommand = new AsyncCommand(RefreshPortsAsync);
        PickProgrammerCommand = new AsyncCommand(PickProgrammerAsync);
        ConnectCommand = new AsyncCommand(ConnectAsync);
        DisconnectCommand = new RelayCommand(Disconnect);
        LoadPartitionsCommand = new AsyncCommand(() => RunAsync(LoadPartitionsAsync));
        ReadCommand = new AsyncCommand(() => RunAsync(ReadAsync));
        WriteCommand = new AsyncCommand(() => RunAsync(WriteAsync));
        EraseCommand = new AsyncCommand(() => RunAsync(EraseAsync));
        ResetCommand = new ParameterCommand(mode => RunFirehoseReset(mode));
    }

    public AsyncCommand RefreshPortsCommand { get; }
    public AsyncCommand PickProgrammerCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public AsyncCommand LoadPartitionsCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand WriteCommand { get; }
    public AsyncCommand EraseCommand { get; }
    public ParameterCommand ResetCommand { get; }

    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<EdlPartitionRowViewModel> Partitions { get; } = [];

    public string? SelectedPort
    {
        get => _selectedPort;
        set => SetProperty(ref _selectedPort, value);
    }

    public string ProgrammerPath { get => _programmerPath; set => SetProperty(ref _programmerPath, value); }
    public string DeviceInfo { get => _deviceInfo; set => SetProperty(ref _deviceInfo, value); }
    public EdlPartitionRowViewModel? SelectedPartition
    {
        get => _selectedPartition;
        set => SetProperty(ref _selectedPartition, value);
    }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private async Task RefreshPortsAsync()
    {
        Ports.Clear();
        foreach (var p in EdlSession.ListEdlPorts()) Ports.Add(p);
        if (Ports.Count > 0 && SelectedPort is null) SelectedPort = Ports[0];
        Status = Ports.Count > 0
            ? $"发现 {Ports.Count} 个串口（进 9008 模式后刷新）。"
            : "未发现 ttyUSB/ttyACM 串口。设备进 EDL 后请确认 qcserial 驱动已绑定。";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task PickProgrammerAsync()
    {
        var f = await _pickers.PickOpenFileAsync("选择 firehose programmer（.mbn / .elf）", "mbn", "elf", "bin").ConfigureAwait(true);
        if (f is not null) ProgrammerPath = f;
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPort))
            throw new InvalidOperationException("请选择 EDL 串口。");
        if (!File.Exists(ProgrammerPath))
            throw new InvalidOperationException("请选择 firehose programmer 文件。");

        Disconnect();
        Status = "连接中…";
        _session = await Task.Run(() => EdlSession.Connect(SelectedPort, ProgrammerPath,
            m => _log($"[EDL] {m}"))).ConfigureAwait(true);
        DeviceInfo = $"MSM HWID: {_session.DeviceInfo.MsmHwid}\n" +
                     $"OEM PK Hash: {_session.DeviceInfo.OemPkHash}\n" +
                     $"Serial: {_session.DeviceInfo.SerialNum}\n" +
                     $"SBL Version: {_session.DeviceInfo.SblVersion}";
        Status = "✓ 已连接（Firehose 就绪）。可读取分区表。";
        await LoadPartitionsAsync().ConfigureAwait(true);
    }

    private void Disconnect()
    {
        _session?.Dispose();
        _session = null;
        Partitions.Clear();
    }

    private async Task LoadPartitionsAsync()
    {
        if (_session is null) { Status = "请先连接设备。"; return; }
        Partitions.Clear();
        var list = await Task.Run(() => _session.GetPartitions(m => _log($"[EDL] {m}"))).ConfigureAwait(true);
        foreach (var p in list)
        {
            Partitions.Add(new EdlPartitionRowViewModel
            {
                Label = p.Label,
                Size = FormatSize(p.SizeBytes),
                Lun = p.Lun,
                Partition = p,
            });
        }
        Status = $"✓ 共 {Partitions.Count} 个分区。";
    }

    private async Task ReadAsync()
    {
        if (_session is null || SelectedPartition is null) { Status = "请先连接并选择分区。"; return; }
        var savePath = await _pickers.PickSaveFileAsync($"保存分区 {SelectedPartition.Label}",
            $"{SelectedPartition.Label}.img").ConfigureAwait(true);
        if (savePath is null) return;

        var partition = SelectedPartition.Partition;
        Status = $"正在读取 {partition.Label}…";
        await Task.Run(() => _session.ReadPartition(partition, savePath,
            p => { Percent = p * 100; Status = $"读取 {partition.Label}… {p:P1}"; })).ConfigureAwait(true);
        Percent = 100;
        Status = $"✓ 已保存到 {savePath}";
        _log($"[EDL] 读取完成：{partition.Label} → {savePath}");
    }

    private async Task WriteAsync()
    {
        if (_session is null || SelectedPartition is null) { Status = "请先连接并选择分区。"; return; }
        var file = await _pickers.PickOpenFileAsync($"选择写入 {SelectedPartition.Label} 的镜像", "img", "bin").ConfigureAwait(true);
        if (file is null) return;

        var partition = SelectedPartition.Partition;
        Status = $"正在写入 {partition.Label}…";
        await Task.Run(() => _session.WritePartition(partition, file,
            p => { Percent = p * 100; Status = $"写入 {partition.Label}… {p:P1}"; })).ConfigureAwait(true);
        Percent = 100;
        Status = $"✓ {partition.Label} 写入完成。";
        _log($"[EDL] 写入完成：{partition.Label} ← {file}");
    }

    private async Task EraseAsync()
    {
        if (_session is null || SelectedPartition is null) { Status = "请先连接并选择分区。"; return; }
        var partition = SelectedPartition.Partition;
        await Task.Run(() => _session.ErasePartition(partition, m => _log($"[EDL] {m}"))).ConfigureAwait(true);
        Status = $"✓ {partition.Label} 已擦除。";
    }

    private async void RunFirehoseReset(string? mode)
    {
        try
        {
            if (_session is null) { Status = "请先连接设备。"; return; }
            await Task.Run(() => _session.Reset(mode ?? "reset", m => _log($"[EDL] {m}"))).ConfigureAwait(true);
            Status = $"✓ 已发送 {mode ?? "reset"}，可断开连接。";
            Disconnect();
        }
        catch (Exception ex)
        {
            Status = $"出错：{ex.Message}";
            _log($"[EDL] {ex}");
        }
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
            _log($"[EDL] {ex}");
        }
    }

    private static string FormatSize(ulong bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MiB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GiB",
    };
}
