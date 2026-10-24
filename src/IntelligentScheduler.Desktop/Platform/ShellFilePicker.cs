using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>Layer-shell panels use GTK's system chooser; ordinary windows keep their platform picker.</summary>
internal static class ShellFilePicker
{
    public static async Task<IReadOnlyList<string>> OpenFilesAsync(TopLevel owner, FilePickerOpenOptions options)
    {
        if (owner.PlatformImpl is LinuxLayerWindow)
            return await LinuxFilePicker.ShowAsync(owner, 0, options, options.FileTypeFilter, options.AllowMultiple);
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        try { return files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray(); }
        finally { foreach (var file in files) file.Dispose(); }
    }

    public static async Task<string?> SaveFileAsync(TopLevel owner, FilePickerSaveOptions options)
    {
        if (owner.PlatformImpl is LinuxLayerWindow)
            return (await LinuxFilePicker.ShowAsync(owner, 1, options, options.FileTypeChoices, false,
                options.SuggestedFileName, options.DefaultExtension)).FirstOrDefault();
        using var file = await owner.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public static async Task<IReadOnlyList<string>> OpenFoldersAsync(TopLevel owner, FolderPickerOpenOptions options)
    {
        if (owner.PlatformImpl is LinuxLayerWindow)
            return await LinuxFilePicker.ShowAsync(owner, 2, options, null, options.AllowMultiple);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        try { return folders.Select(folder => folder.TryGetLocalPath()).OfType<string>().ToArray(); }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }
}
