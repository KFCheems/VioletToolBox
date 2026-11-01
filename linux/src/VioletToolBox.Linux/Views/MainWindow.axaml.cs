using Avalonia.Controls;
using Avalonia.Interactivity;
using VioletToolBox.Linux.ViewModels;

namespace VioletToolBox.Linux.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePickers();
        WirePickers();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void WirePickers()
    {
        if (Vm is null) return;
        Vm.SetPickers(
            PickOpenFileAsync,
            PickSaveFileAsync,
            PickFolderAsync);

        // Sync the sidebar highlight with the startup page chosen via --page.
        for (var i = 0; i < Vm.NavItems.Count; i++)
        {
            if (ReferenceEquals(Vm.NavItems[i].Page, Vm.CurrentPage))
                NavList.SelectedIndex = i;
        }

        _ = SeedDeviceSummaryAsync();
    }

    private async System.Threading.Tasks.Task SeedDeviceSummaryAsync()
    {
        // Lightweight startup probe so the status bar reflects tool availability.
        await System.Threading.Tasks.Task.Delay(1500).ConfigureAwait(true);
        if (Vm is not null)
        {
            Vm.DeviceSummary = "提示：进入「设备」页点击刷新检测设备。";
        }
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is null || sender is not ListBox { SelectedItem: NavItem item }) return;
        Vm.CurrentPage = item.Page;
    }

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
