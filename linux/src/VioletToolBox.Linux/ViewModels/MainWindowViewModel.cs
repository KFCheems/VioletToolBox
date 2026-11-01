using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace VioletToolBox.Linux.ViewModels;

public sealed record NavItem(string Title, string Glyph, ViewModelBase Page);

public sealed class MainWindowViewModel : ViewModelBase, IFilePickers
{
    private ViewModelBase _currentPage;
    private string _logText = $"紫罗兰工具箱 · Linux 移植版（{DateTime.Now:yyyy-MM-dd HH:mm}）\n";
    private string _deviceSummary = "未检测";

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
            new NavItem("设备", "⌂", DevicePage),
            new NavItem("投屏", "▶", ScreenPage),
            new NavItem("刷入", "⚡", FlashPage),
            new NavItem("分区", "▤", PartitionPage),
            new NavItem("欧加线刷", "◈", OujiaPage),
            new NavItem("EDL", "◉", EdlPage),
            new NavItem("降级助手", "⇩", DowngradePage),
            new NavItem("模块专区", "⊕", ModulePage),
            new NavItem("应用与文件", "☰", AppsPage),
            new NavItem("脱机修补", "⚒", BootPatchPage),
            new NavItem("安卓通用", "⚙", GenericPage),
            new NavItem("解包", "▣", ExtractionPage),
            new NavItem("备份助手", "🕮", BackupPage),
            new NavItem("下载专区", "⇓", DownloadCenterPage),
            new NavItem("下载", "↓", DownloadPage),
            new NavItem("关于", "ℹ", AboutPage),
        ];

        var requested = Program.InitialPage;
        _currentPage = string.IsNullOrWhiteSpace(requested)
            ? DevicePage
            : NavItems.FirstOrDefault(n => n.Title.Equals(requested, StringComparison.OrdinalIgnoreCase)
                                        || n.Title.StartsWith(requested, StringComparison.OrdinalIgnoreCase))?.Page
              ?? DevicePage;
    }

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

    public void AppendLog(string message)
    {
        LogText += $"{DateTime.Now:HH:mm:ss}  {message}\n";
    }

    // IFilePickers — implemented by MainWindow setting these delegates on startup.
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
