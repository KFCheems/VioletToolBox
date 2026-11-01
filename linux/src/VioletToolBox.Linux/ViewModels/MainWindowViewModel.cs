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
        FlashPage = new FlashPageViewModel(AppendLog, this);
        PartitionPage = new PartitionPageViewModel(AppendLog, this);
        ExtractionPage = new ExtractionPageViewModel(AppendLog, this);
        EdlPage = new EdlPageViewModel(AppendLog, this);
        DownloadPage = new DownloadPageViewModel(AppendLog, this);

        NavItems =
        [
            new NavItem("设备", "⌂", DevicePage),
            new NavItem("刷入", "⚡", FlashPage),
            new NavItem("分区", "▤", PartitionPage),
            new NavItem("解包", "▣", ExtractionPage),
            new NavItem("EDL", "◉", EdlPage),
            new NavItem("下载", "↓", DownloadPage),
        ];

        var requested = Program.InitialPage;
        _currentPage = string.IsNullOrWhiteSpace(requested)
            ? DevicePage
            : NavItems.FirstOrDefault(n => n.Title.Equals(requested, StringComparison.OrdinalIgnoreCase)
                                        || n.Title.StartsWith(requested, StringComparison.OrdinalIgnoreCase))?.Page
              ?? DevicePage;
    }

    public DevicePageViewModel DevicePage { get; }
    public FlashPageViewModel FlashPage { get; }
    public PartitionPageViewModel PartitionPage { get; }
    public ExtractionPageViewModel ExtractionPage { get; }
    public EdlPageViewModel EdlPage { get; }
    public DownloadPageViewModel DownloadPage { get; }

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
