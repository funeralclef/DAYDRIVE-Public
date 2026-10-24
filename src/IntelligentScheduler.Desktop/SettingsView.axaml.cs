using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Desktop.ViewModels;

namespace IntelligentScheduler.Desktop;

public sealed partial class SettingsView : UserControl
{
    private EdgeShellViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
        // Tunnelled, and for two reasons. A key press that is being read as a shortcut must not
        // also be typed into whatever happens to have focus — and Enter, Tab and Escape never
        // reach a bubbling handler at all, because the focused control answers them first.
        AddHandler(KeyDownEvent, OnCapturingKeyDown, RoutingStrategies.Tunnel);
        // A page that is left listening is a page that eats the next key press the user makes
        // for some entirely different reason.
        DetachedFromVisualTree += (_, _) => _viewModel?.CancelShortcutCapture();
    }

    /// <summary>
    /// A shortcut being rebound. While a row is listening this window's keyboard belongs to it:
    /// every press is either the answer, the way out, or nothing — and in all three cases it
    /// stops here rather than going on to whatever has focus.
    /// </summary>
    private void OnCapturingKeyDown(object? sender, KeyEventArgs args)
    {
        if (_viewModel is not { IsCapturingShortcut: true } viewModel) return;
        args.Handled = true;
        if (args.Key == Key.Escape)
        {
            viewModel.CancelShortcutCapture();
            return;
        }
        // Null is a modifier held on its own, or a combination that would make a poor global
        // shortcut. Either way the row goes on listening: the user is mid-gesture.
        if (ShortcutGesture.Read(args.KeyModifiers, args.Key) is { } gesture)
            viewModel.ApplyCapturedShortcut(gesture);
    }

    private void Attach()
    {
        if (ReferenceEquals(_viewModel, DataContext)) return;
        Detach();
        _viewModel = DataContext as EdgeShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CustomModelPickRequested += OnCustomModelPickRequested;
            _viewModel.CustomFontPickRequested += OnCustomFontPickRequested;
            _viewModel.CloudFolderPickRequested += OnCloudFolderPickRequested;
            _viewModel.ThemeFileRequested += OnThemeFileRequested;
        }
    }

    private void Detach()
    {
        if (_viewModel is null) return;
        _viewModel.CustomModelPickRequested -= OnCustomModelPickRequested;
        _viewModel.CustomFontPickRequested -= OnCustomFontPickRequested;
        _viewModel.CloudFolderPickRequested -= OnCloudFolderPickRequested;
        _viewModel.ThemeFileRequested -= OnThemeFileRequested;
        _viewModel = null;
    }

    private async void OnCustomFontPickRequested(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var files = await ShellFilePicker.OpenFilesAsync(topLevel, new FilePickerOpenOptions
            {
                Title = _viewModel.FontDialogTitle,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("TTF / OTF") { Patterns = ["*.ttf", "*.otf"] },
                    FilePickerFileTypes.All
                ]
            });
            if (files.Count > 0) _viewModel.ImportCustomFont(files[0]);
        }
        catch (Exception exception)
        {
            _viewModel.ReportUnexpectedError(exception);
        }
    }

    private async void OnThemeFileRequested(object? sender, bool export)
    {
        if (_viewModel is not { } model || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var type = new FilePickerFileType("DAYDRIVE") { Patterns = ["*.daydrive-theme.json", "*.json"] };
            if (export)
            {
                var path = await ShellFilePicker.SaveFileAsync(topLevel, new FilePickerSaveOptions
                {
                    Title = model.ExportThemeTitle, SuggestedFileName = "MARELIAN.daydrive-theme.json",
                    FileTypeChoices = [type], DefaultExtension = "json"
                });
                if (path is null) return;
                await using var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
                await using var writer = new System.IO.StreamWriter(stream);
                await writer.WriteAsync(model.ExportAppearance());
                model.ThemeExported();
            }
            else
            {
                var files = await ShellFilePicker.OpenFilesAsync(topLevel, new FilePickerOpenOptions
                {
                    Title = model.ImportThemeTitle, AllowMultiple = false, FileTypeFilter = [type]
                });
                if (files.Count == 0) return;
                await using var stream = System.IO.File.OpenRead(files[0]);
                using var reader = new System.IO.StreamReader(stream);
                var buffer = new char[256 * 1024 + 1];
                var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                if (count == buffer.Length) { model.ThemeFileFailed(); return; }
                model.ImportAppearance(new string(buffer, 0, count));
            }
        }
        catch (Exception) { model.ThemeFileFailed(); }
    }

    /// <summary>Picks the directory a cloud client already mirrors.</summary>
    private async void OnCloudFolderPickRequested(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var folders = await ShellFilePicker.OpenFoldersAsync(topLevel, new FolderPickerOpenOptions
            {
                Title = _viewModel.CloudFolderDialogTitle,
                AllowMultiple = false
            });
            if (folders.Count > 0) _viewModel.SetCloudFolder(folders[0]);
        }
        catch (Exception exception)
        {
            _viewModel.ReportUnexpectedError(exception);
        }
    }

    /// <summary>The file picker lives on the top level, so the view opens it for the view model.</summary>
    private async void OnCustomModelPickRequested(object? sender, bool isProjector)
    {
        if (_viewModel is null) return;
        try
        {
            await PickAsync(_viewModel, isProjector);
        }
        catch (Exception exception)
        {
            _viewModel.ReportUnexpectedError(exception);
        }
    }

    private async Task PickAsync(EdgeShellViewModel viewModel, bool isProjector)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await ShellFilePicker.OpenFilesAsync(topLevel, new FilePickerOpenOptions
        {
            Title = viewModel.CustomModelDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GGUF") { Patterns = ["*.gguf"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0) viewModel.SetCustomModelFile(files[0], isProjector);
    }
}
