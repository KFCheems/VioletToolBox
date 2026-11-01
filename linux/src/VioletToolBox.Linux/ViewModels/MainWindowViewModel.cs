using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace VioletToolBox.Linux.ViewModels;

public sealed record NavItem(string Title, string Glyph, string Category, ViewModelBase Page);

public sealed class MainWindowViewModel : ViewModelBase, IFilePickers
{
    private ViewModelBase _currentPage;
    private string _logText = $"紫罗兰工具箱 · Linux 移植版（{DateTime.Now:yyyy-MM-dd HH:mm}）\n";
    private string _deviceSummary = "未检测";
    private bool _isLogExpanded = true;

    public MainWindowViewModel()
    {
        DevicePage = new DevicePageViewModel();
        ScreenPage = new ScreenPageViewModel(AppendLog);
        FlashPage = new FlashPageViewModel(AppendLog, this);
        PartitionPage = new PartitionPageViewModel(AppendLog, this);
        OujiaPage = new OujiaPageViewModel(AppendLog, this);
        EdlPage = new EdlPageViewModel(AppendLog, this);
        DowngradePage = new DowngradePageViewModel(AppendLog, this);
        ModulePage = new ModulePageViewModel(AppendLog, this);
        AppsPage = new AppsPageViewModel(AppendLog, this);
        BootPatchPage = new BootPatchPageViewModel(AppendLog, this);
        GenericPage = new GenericPageViewModel(AppendLog, this);
        ExtractionPage = new ExtractionPageViewModel(AppendLog, this);
        BackupPage = new BackupPageViewModel(AppendLog, this);
        DownloadCenterPage = new DownloadCenterPageViewModel(AppendLog);
        DownloadPage = new DownloadPageViewModel(AppendLog, this);
        AboutPage = new AboutPageViewModel();

        NavItems =
        [
            // 基础操作
            new NavItem("设备主页", "⌂", "基础与连接", DevicePage),
            new NavItem("实时投屏", "▶", "基础与连接", ScreenPage),
            new NavItem("应用文件", "☰", "基础与连接", AppsPage),
            new NavItem("备份助手", "🕮", "基础与连接", BackupPage),

            // 刷写专区
            new NavItem("基本刷入", "⚡", "刷机与线刷", FlashPage),
            new NavItem("可视分区", "▤", "刷机与线刷", PartitionPage),
            new NavItem("欧加线刷", "◈", "刷机与线刷", OujiaPage),
            new NavItem("EDL (9008)", "◉", "刷机与线刷", EdlPage),

            // 深度玩机与ROOT
            new NavItem("模块专区", "⊕", "玩机与修补", ModulePage),
            new NavItem("脱机修补", "⚒", "玩机与修补", BootPatchPage),
            new NavItem("安卓通用", "⚙", "玩机与修补", GenericPage),
            new NavItem("固件解包", "▣", "玩机与修补", ExtractionPage),

            // 固件与下载
            new NavItem("降级助手", "⇩", "下载与服务", DowngradePage),
            new NavItem("下载专区", "⇓", "下载与服务", DownloadCenterPage),
            new NavItem("断点续传", "↓", "下载与服务", DownloadPage),
            new NavItem("关于箱子", "ℹ", "下载与服务", AboutPage),
        ];

        ToggleLogCommand = new RelayCommand(() => IsLogExpanded = !IsLogExpanded);
        ClearLogCommand = new RelayCommand(() => LogText = $"[已清空日志 {DateTime.Now:HH:mm:ss}]\n");

        _currentPage = ResolveInitialPage();

    }

    public RelayCommand ToggleLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public DevicePageViewModel DevicePage { get; }
    public ScreenPageViewModel ScreenPage { get; }
    public FlashPageViewModel FlashPage { get; }
    public PartitionPageViewModel PartitionPage { get; }
    public OujiaPageViewModel OujiaPage { get; }
    public EdlPageViewModel EdlPage { get; }
    public DowngradePageViewModel DowngradePage { get; }
    public ModulePageViewModel ModulePage { get; }
    public AppsPageViewModel AppsPage { get; }
    public BootPatchPageViewModel BootPatchPage { get; }
    public GenericPageViewModel GenericPage { get; }
    public ExtractionPageViewModel ExtractionPage { get; }
    public BackupPageViewModel BackupPage { get; }
    public DownloadCenterPageViewModel DownloadCenterPage { get; }
    public DownloadPageViewModel DownloadPage { get; }
    public AboutPageViewModel AboutPage { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Resolves the --page startup request against upstream page titles (case-insensitive).</summary>
    private ViewModelBase ResolveInitialPage()
    {
        var requested = Program.InitialPage?.Trim();
        if (string.IsNullOrWhiteSpace(requested)) return DevicePage;
        return requested.ToLowerInvariant() switch
        {
            "主页" or "首页" or "home" => DevicePage,
            "投屏" => ScreenPage,
            "基本刷入" => FlashPage,
            "可视刷写" or "可视分区" => PartitionPage,
            "欧加线刷" or "欧加刷机" => OujiaPage,
            "edl刷写" or "edl" => EdlPage,
            "降级助手" => DowngradePage,
            "模块专区" => ModulePage,
            "断点续传" => DownloadPage,
            "文件传输" => AppsPage,
            "脱机修补" => BootPatchPage,
            "应用管理" => AppsPage,
            "安卓通用" => GenericPage,
            "payload" or "解包" => ExtractionPage,
            "备份助手" => BackupPage,
            "rom专区" or "镜像下载" or "联想查包" or "下载专区" => DownloadCenterPage,
            "关于" => AboutPage,
            _ => DevicePage,
        };
    }

    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }

    public string DeviceSummary
    {
        get => _deviceSummary;
        set => SetProperty(ref _deviceSummary, value);
    }

    public bool IsLogExpanded
    {
        get => _isLogExpanded;
        set
        {
            if (SetProperty(ref _isLogExpanded, value))
                Raise(nameof(LogToggleText));
        }
    }

    public string LogToggleText => IsLogExpanded ? "▲ 收起日志" : "▼ 展开日志";

    public void AppendLog(string message)
    {
        LogText += $"{DateTime.Now:HH:mm:ss}  {message}\n";
    }

    // IFilePickers
    private Func<string, string[], Task<string?>>? _pickOpenFile;
    private Func<string, string, Task<string?>>? _pickSaveFile;
    private Func<string, Task<string?>>? _pickFolder;

    public void SetPickers(
        Func<string, string[], Task<string?>> pickOpenFile,
        Func<string, string, Task<string?>> pickSaveFile,
        Func<string, Task<string?>> pickFolder)
    {
        _pickOpenFile = pickOpenFile;
        _pickSaveFile = pickSaveFile;
        _pickFolder = pickFolder;
    }

    async Task<string?> IFilePickers.PickOpenFileAsync(string title, params string[] patterns)
        => _pickOpenFile is null ? null : await _pickOpenFile(title, patterns).ConfigureAwait(true);

    async Task<string?> IFilePickers.PickSaveFileAsync(string title, string suggestedName)
        => _pickSaveFile is null ? null : await _pickSaveFile(title, suggestedName).ConfigureAwait(true);

    async Task<string?> IFilePickers.PickFolderAsync(string title)
        => _pickFolder is null ? null : await _pickFolder(title).ConfigureAwait(true);
}
