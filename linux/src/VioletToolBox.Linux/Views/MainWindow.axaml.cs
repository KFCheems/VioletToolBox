using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VioletToolBox.Linux.ViewModels;

namespace VioletToolBox.Linux.Views;

public partial class MainWindow : Window
{
    // Map (list, index) → page VM, mirroring the upstream 18-item navigation.
    private readonly List<(ListBox List, int Index, Func<MainWindowViewModel, ViewModelBase> Page)> _navMap = [];
    private bool _wiring;
    private bool _opened;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireNav();
        WireNav();
        KeyDown += OnKeyDownAccelerators;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void WireNav()
    {
        if (Vm is null) return;
        Vm.SetPickers(PickOpenFileAsync, PickSaveFileAsync, PickFolderAsync);


        _navMap.Clear();
        _navMap.Add((TopNav, 0, vm => vm.DevicePage));      // 主页
        _navMap.Add((TopNav, 1, vm => vm.ScreenPage));      // 投屏
        _navMap.Add((FlashNav, 0, vm => vm.FlashPage));     // 基本刷入
        _navMap.Add((FlashNav, 1, vm => vm.PartitionPage)); // 可视刷写
        _navMap.Add((FlashNav, 2, vm => vm.OujiaPage));     // 欧加线刷
        _navMap.Add((FlashNav, 3, vm => vm.EdlPage));       // EDL刷写
        _navMap.Add((FlashNav, 4, vm => vm.DowngradePage)); // 降级助手
        _navMap.Add((UtilNav, 0, vm => vm.ModulePage));     // 模块专区
        _navMap.Add((UtilNav, 1, vm => vm.DownloadPage));   // 断点续传
        _navMap.Add((UtilNav, 2, vm => vm.AppsPage));       // 文件传输（应用与文件页承载）
        _navMap.Add((UtilNav, 3, vm => vm.BootPatchPage));  // 脱机修补
        _navMap.Add((UtilNav, 4, vm => vm.AppsPage));       // 应用管理
        _navMap.Add((UtilNav, 5, vm => vm.GenericPage));    // 安卓通用
        _navMap.Add((UtilNav, 6, vm => vm.ExtractionPage)); // Payload
        _navMap.Add((UtilNav, 7, vm => vm.BackupPage));     // 备份助手
        _navMap.Add((ResNav, 0, vm => vm.DownloadCenterPage)); // Rom专区
        _navMap.Add((ResNav, 1, vm => vm.DownloadCenterPage)); // 镜像下载
        _navMap.Add((ResNav, 2, vm => vm.DownloadCenterPage)); // 联想查包
        _navMap.Add((AboutNav, 0, vm => vm.AboutPage));     // 关于

        // Sync selection with --page startup; VM page stays the single source of truth.
        _wiring = true;
        var startupPage = Vm.CurrentPage;
        SelectForPage(startupPage);
        Vm.CurrentPage = startupPage; // re-assert in case selection events mutated it
        _wiring = false;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The container-generation auto-selection happens before OnOpened (the
        // user cannot click an invisible window), so gate on that instead of
        // fighting the framework afterwards.
        if (_wiring || !_opened || Vm is null || sender is not ListBox list || list.SelectedIndex < 0) return;

        var entry = _navMap.FirstOrDefault(m => m.List == list && m.Index == list.SelectedIndex);
        if (entry.List is null) return;

        _wiring = true;
        ClearOtherSelections(list);
        Vm.CurrentPage = entry.Page(Vm);
        _wiring = false;
    }

    /// <summary>Points every nav list's highlight at the current page (no page mutation).</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Layout/templates have settled by the first opened pass; sync highlight
        // to the startup page, then start honouring selections (real clicks).
        if (Vm is { } vm)
        {
            _wiring = true;
            SelectForPage(vm.CurrentPage);
            _wiring = false;
            _opened = true;
        }
    }

    private void SelectForPage(ViewModelBase page)
    {
        foreach (var (list, index, getter) in _navMap)
        {
            if (ReferenceEquals(getter(Vm!), page))
            {
                list.SelectedIndex = index;
                return;
            }
        }
        TopNav.SelectedIndex = 0;
    }

    private void ClearOtherSelections(ListBox keep)
    {
        foreach (var list in new[] { TopNav, FlashNav, UtilNav, ResNav, AboutNav })
            if (list != keep) list.SelectedIndex = -1;
    }

    // ---- group collapse (upstream chevron behavior) ----
    private void OnToggleFlashGroup(object? sender, RoutedEventArgs e) => ToggleGroup(FlashNav, FlashGroupHeader);
    private void OnToggleUtilGroup(object? sender, RoutedEventArgs e) => ToggleGroup(UtilNav, UtilGroupHeader);
    private void OnToggleResGroup(object? sender, RoutedEventArgs e) => ToggleGroup(ResNav, ResGroupHeader);

    private void ToggleGroup(ListBox group, Button header)
    {
        group.IsVisible = !group.IsVisible;
        header.Content = (group.IsVisible ? "▾ " : "▸ ") + (header.Content as string)?.TrimStart('▾', '▸', ' ');
    }

    // ---- title bar buttons ----
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ---- footer shortcuts ----
    private async void OnOpenTerminal(object? sender, RoutedEventArgs e)
    {
        // Upstream opens CMD.exe in platform-tools; Linux equivalent: a shell in the app dir.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/konsole",
                Arguments = $"--workdir \"{AppContext.BaseDirectory}\"",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            Vm?.AppendLog("[终端] 未找到终端模拟器（konsole）。");
        }
    }

    private async void OnToolsCheck(object? sender, RoutedEventArgs e)
    {
        // Jump to the environment check on the About page (upstream: 设备管理器).
        if (Vm is null) return;
        _wiring = true;
        ClearOtherSelections(AboutNav);
        AboutNav.SelectedIndex = 0;
        Vm.CurrentPage = Vm.AboutPage;
        _wiring = false;
    }

    private void OnKeyDownAccelerators(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            var flat = new[]
            {
                (TopNav, 0), (TopNav, 1),
                (FlashNav, 0), (FlashNav, 1), (FlashNav, 2), (FlashNav, 3), (FlashNav, 4),
                (UtilNav, 0), (UtilNav, 1),
            };
            var index = (int)(e.Key - Key.D1);
            if (index < flat.Length)
            {
                var (list, i) = flat[index];
                list.SelectedIndex = i;
                e.Handled = true;
            }
        }
    }

    // ---- file pickers (unchanged behavior) ----
    private async System.Threading.Tasks.Task<string?> PickOpenFileAsync(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("镜像 / 固件文件")
                {
                    Patterns = patterns.Length > 0 ? patterns.Select(p => "*." + p).ToArray() : ["*"],
                },
                new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async System.Threading.Tasks.Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        }).ConfigureAwait(true);
        return file?.Path.LocalPath;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}
