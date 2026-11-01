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
    private string _deviceInfo = "";
    private string _adbSummary = "ADB：—";
    private string _fastbootSummary = "Fastboot：—";

    // Upstream 主页: two-column field card, every field individually bound.
    private string _deviceStatus = "未连接";
    private string _connectionType = "等待设备连接...";
    private string _serial = "--";
    private string _model = "--";
    private string _deviceCode = "--";
    private string _androidVersion = "--";
    private string _unlockState = "--";
    private string _versionInfo = "--";
    private string _kernelVersion = "--";
    private string _buildDate = "--";
    private string _slotSuffix = "--";
    private string _cpuVendor = "--";
    private string _socName = "--";
    private string _osName = "--";
    private string _selinuxState = "--";

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
    public string DeviceStatus { get => _deviceStatus; set => SetProperty(ref _deviceStatus, value); }
    public string ConnectionType { get => _connectionType; set => SetProperty(ref _connectionType, value); }
    public string Serial { get => _serial; set => SetProperty(ref _serial, value); }
    public string Model { get => _model; set => SetProperty(ref _model, value); }
    public string DeviceCode { get => _deviceCode; set => SetProperty(ref _deviceCode, value); }
    public string AndroidVersion { get => _androidVersion; set => SetProperty(ref _androidVersion, value); }
    public string UnlockState { get => _unlockState; set => SetProperty(ref _unlockState, value); }
    public string VersionInfo { get => _versionInfo; set => SetProperty(ref _versionInfo, value); }
    public string KernelVersion { get => _kernelVersion; set => SetProperty(ref _kernelVersion, value); }
    public string BuildDate { get => _buildDate; set => SetProperty(ref _buildDate, value); }
    public string SlotSuffix { get => _slotSuffix; set => SetProperty(ref _slotSuffix, value); }
    public string CpuVendor { get => _cpuVendor; set => SetProperty(ref _cpuVendor, value); }
    public string SocName { get => _socName; set => SetProperty(ref _socName, value); }
    public string OsName { get => _osName; set => SetProperty(ref _osName, value); }
    public string SelinuxState { get => _selinuxState; set => SetProperty(ref _selinuxState, value); }
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
            string Get(params string[] keys) =>
                keys.FirstOrDefault(k => props.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) is { } hit
                    ? props[hit] : "--";
            string GetOr(params string[] keys) => Get(keys) == "--" ? "" : Get(keys);

            DeviceStatus = "已连接";
            ConnectionType = "ADB (开机模式)";
            Serial = device.Serial;
            Model = Get("ro.product.model");
            DeviceCode = Get("ro.product.device");
            AndroidVersion = Get("ro.build.version.release");
            UnlockState = props.TryGetValue("ro.boot.unlocked", out var unl)
                ? (unl == "1" ? "已解锁" : "未解锁")
                : props.TryGetValue("ro.boot.flash.locked", out var locked) ? (locked == "0" ? "已解锁" : "未解锁") : "--";
            VersionInfo = Get("ro.build.version.incremental");
            KernelVersion = "--";
            BuildDate = Get("ro.build.date");
            SlotSuffix = Get("ro.boot.slot_suffix", "ro.boot.slot");
            CpuVendor = "--";
            SocName = "--";
            OsName = Get("ro.build.version.oplusrom.release", "ro.build.display.id");
            SelinuxState = "--";

            // Kernel / CPU / SELinux need shell reads; fetch in one pass.
            try
            {
                var extra = await _adb.ShellAsync(device.Serial,
                    "uname -r; echo ---; getenforce; echo ---; head -n 12 /proc/cpuinfo").ConfigureAwait(true);
                var parts = extra.Split("---");
                if (parts.Length > 0) KernelVersion = parts[0].Trim();
                if (parts.Length > 1) SelinuxState = parts[1].Trim() switch
                {
                    "Enforcing" => "Enforcing (严格)",
                    "Permissive" => "Permissive (宽容)",
                    var v => string.IsNullOrEmpty(v) ? "--" : v,
                };
                if (parts.Length > 2)
                {
                    var cpu = parts[2];
                    CpuVendor = cpu.Contains("MediaTek") ? "联发科" : cpu.Contains("Qualcomm") ? "高通" : "--";
                    var soc = System.Text.RegularExpressions.Regex.Match(cpu, @"(SM\d+|MT\d+)");
                    if (soc.Success) SocName = soc.Value;
                }
            }
            catch { /* best-effort extras */ }
        }
        catch (Exception ex)
        {
            DeviceStatus = "读取失败";
            _deviceInfo = $"读取设备信息失败：{ex.Message}";
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
