using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Linux.ViewModels;

public sealed record ToolStatus(string Name, string Status, string? Path)
{
    public string Display => Path is null ? $"{Name}  ✗ 未找到" : $"{Name}  ✓ {Path}";
}

public sealed class DevicePageViewModel : ViewModelBase
{
    private readonly AdbService _adb = new();
    private readonly FastbootService _fastboot = new();

    private AdbDevice? _selectedAdbDevice;
    private string _deviceInfo = "点击「刷新」检测设备。";
    private string _adbSummary = "ADB：—";
    private string _fastbootSummary = "Fastboot：—";

    public DevicePageViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync);
        RebootCommand = new ParameterCommand(target => _ = RebootAsync(target));
        SetSlotCommand = new ParameterCommand(slot => _ = SetSlotAsync(slot));
    }

    public AsyncCommand RefreshCommand { get; }
    public ParameterCommand RebootCommand { get; }
    public ParameterCommand SetSlotCommand { get; }

    public ObservableCollection<AdbDevice> AdbDevices { get; } = [];
    public ObservableCollection<FastbootDevice> FastbootDevices { get; } = [];
    public ObservableCollection<ToolStatus> Tools { get; } = [];

    public AdbDevice? SelectedAdbDevice
    {
        get => _selectedAdbDevice;
        set { if (SetProperty(ref _selectedAdbDevice, value)) _ = LoadDeviceInfoAsync(); }
    }

    public string DeviceInfo { get => _deviceInfo; set => SetProperty(ref _deviceInfo, value); }
    public string AdbSummary { get => _adbSummary; set => SetProperty(ref _adbSummary, value); }
    public string FastbootSummary { get => _fastbootSummary; set => SetProperty(ref _fastbootSummary, value); }

    private async Task RefreshAsync()
    {
        AdbDevices.Clear();
        FastbootDevices.Clear();
        Tools.Clear();

        try
        {
            var adbDevices = await _adb.DevicesAsync().ConfigureAwait(true);
            foreach (var d in adbDevices) AdbDevices.Add(d);
            AdbSummary = $"ADB：{adbDevices.Count(d => d.IsOnline)} 台在线 / {adbDevices.Count} 台";
        }
        catch (Exception ex)
        {
            AdbSummary = $"ADB：不可用（{ex.Message}）";
        }

        try
        {
            var fbDevices = await _fastboot.DevicesAsync().ConfigureAwait(true);
            foreach (var d in fbDevices) FastbootDevices.Add(d);
            FastbootSummary = fbDevices.Count > 0 ? $"Fastboot：{fbDevices.Count} 台" : "Fastboot：无设备";
        }
        catch (Exception ex)
        {
            FastbootSummary = $"Fastboot：不可用（{ex.Message}）";
        }

        foreach (var (name, path) in ToolLocator.Describe())
            Tools.Add(new ToolStatus(name, path is null ? "未找到" : "就绪", path));

        if (AdbDevices.FirstOrDefault(d => d.IsOnline) is { } first)
        {
            SelectedAdbDevice = first;
        }
        else
        {
            DeviceInfo = FastbootDevices.Count > 0
                ? $"已连接 Fastboot 设备：{FastbootDevices[0].DisplayName}\n（信息读取需 ADB 模式）"
                : "未检测到在线设备。请检查 USB 连接与 udev 规则。";
        }
    }

    private async Task LoadDeviceInfoAsync()
    {
        if (SelectedAdbDevice is not { IsOnline: true } device) return;
        try
        {
            var props = await _adb.GetPropsAsync(device.Serial).ConfigureAwait(true);
            string Get(string key) => props.TryGetValue(key, out var v) ? v : "—";
            DeviceInfo =
                $"序列号：{device.Serial}\n" +
                $"型号：{Get("ro.product.model")}（{Get("ro.product.brand")}）\n" +
                $"Android：{Get("ro.build.version.release")}（{Get("ro.build.version.incremental")}）\n" +
                $"ColorOS/系统：{Get("ro.build.version.oplusrom.release")} {Get("ro.build.date")}\n" +
                $"当前槽位：{Get("ro.boot.slot_suffix")}\n" +
                $"已解锁：{Get("ro.boot.verifiedbootstate")} / {Get("sys.oem_unlock_allowed")}";
        }
        catch (Exception ex)
        {
            DeviceInfo = $"读取设备信息失败：{ex.Message}";
        }
    }

    private async Task RebootAsync(string? target)
    {
        try
        {
            if (FastbootDevices.Count > 0)
            {
                await _fastboot.RebootAsync(FastbootDevices[0].Serial, target ?? "").ConfigureAwait(true);
                DeviceInfo = $"已向 Fastboot 设备发送重启（{target ?? "系统"}）。";
            }
            else if (SelectedAdbDevice is { IsOnline: true } adb)
            {
                await _adb.RebootAsync(adb.Serial, target ?? "").ConfigureAwait(true);
                DeviceInfo = $"已发送重启指令（{target ?? "系统"}）。";
            }
            else
            {
                DeviceInfo = "没有可重启的设备。";
            }
        }
        catch (Exception ex)
        {
            DeviceInfo = $"重启失败：{ex.Message}";
        }
    }

    private async Task SetSlotAsync(string? slot)
    {
        if (FastbootDevices.Count == 0)
        {
            DeviceInfo = "切槽需要在 Bootloader/Fastboot 模式下进行。";
            return;
        }
        try
        {
            var result = await _fastboot.SetActiveAsync(FastbootDevices[0].Serial, slot ?? "a").ConfigureAwait(true);
            DeviceInfo = result.Success
                ? $"已切换到槽位 {slot?.ToUpperInvariant()}。"
                : $"切槽失败：{result.Combined}";
        }
        catch (Exception ex)
        {
            DeviceInfo = $"切槽失败：{ex.Message}";
        }
    }
}
