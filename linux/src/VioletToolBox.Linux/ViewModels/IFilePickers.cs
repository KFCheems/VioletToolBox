using System.Threading.Tasks;

namespace VioletToolBox.Linux.ViewModels;

/// <summary>File/folder pickers, implemented by MainWindow through StorageProvider.</summary>
public interface IFilePickers
{
    Task<string?> PickOpenFileAsync(string title, params string[] patterns);
    Task<string?> PickSaveFileAsync(string title, string suggestedName);
    Task<string?> PickFolderAsync(string title);
}
