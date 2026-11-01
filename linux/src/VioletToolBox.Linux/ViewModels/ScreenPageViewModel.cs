using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using VioletToolBox.Core.Android;

namespace VioletToolBox.Linux.ViewModels;

public sealed class ScreenPageViewModel : ViewModelBase
{
    private readonly ScrcpyService _scrcpy = new();
    private readonly Action<string> _log;

    private int _maxSize = 1600;
    private int _bitrate = 8;
    private int _maxFps = 60;
    private bool _turnScreenOff;
    private bool _stayAwake;
    private bool _showTouches;
    private Process? _current;

    public ScreenPageViewModel(Action<string> log)
    {
        _log = log;
        StartCommand = new AsyncCommand(StartAsync);
        StopCommand = new RelayCommand(() =>
        {
            if (_current is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
                _log("[投屏] 已停止。");
            }
        });
    }

    public AsyncCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    public ObservableCollection<int> SizeOptions { get; } = [0, 800, 1200, 1600, 1920, 2560];
    public ObservableCollection<int> FpsOptions { get; } = [0, 30, 60, 90, 120];

    public int MaxSize { get => _maxSize; set => SetProperty(ref _maxSize, value); }
    public int Bitrate { get => _bitrate; set => SetProperty(ref _bitrate, value); }
    public int MaxFps { get => _maxFps; set => SetProperty(ref _maxFps, value); }
    public bool TurnScreenOff { get => _turnScreenOff; set => SetProperty(ref _turnScreenOff, value); }
    public bool StayAwake { get => _stayAwake; set => SetProperty(ref _stayAwake, value); }
    public bool ShowTouches { get => _showTouches; set => SetProperty(ref _showTouches, value); }

    public string Availability => _scrcpy.IsAvailable
        ? "✓ scrcpy 已就绪（投屏窗口为独立进程）"
        : "✗ 未安装 scrcpy，请执行：sudo dnf install scrcpy";

    private async Task StartAsync()
    {
        if (!_scrcpy.IsAvailable)
            throw new InvalidOperationException("请先安装 scrcpy：sudo dnf install scrcpy");
        var adb = await new AdbService().FirstOnlineDeviceAsync().ConfigureAwait(true);
        _current = _scrcpy.Launch(adb?.Serial, new ScrcpyOptions(MaxSize, Bitrate, MaxFps,
            TurnScreenOff, StayAwake, ShowTouches, Wireless: false),
            m => _log($"[投屏] {m}"));
        _log($"[投屏] 已启动（{Bitrate}Mbps / {MaxFps}fps）。");
    }
}
