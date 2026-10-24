using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using IntelligentScheduler.Application;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Desktop.Platform;

namespace IntelligentScheduler.Desktop;

public sealed partial class ChatFlyoutView : UserControl
{
    private EdgeShellViewModel? _viewModel;

    /// <summary>
    /// Whether this is the copy of the view the conversation is currently being held in.
    ///
    /// Two are alive at once and only one is ever on the screen: the sheet inside the planner
    /// and the widened window beside it. They share one view model, so every notice it sends
    /// reaches both — and anything that touches the caret, the clipboard or the scroll position
    /// belongs to exactly one of them. The one that is out of shape says nothing.
    /// </summary>
    private bool IsHostingTheConversation =>
        _viewModel is not null && _viewModel.IsChatExpanded == Classes.Contains("chatDocked");

    public ChatFlyoutView()
    {
        InitializeComponent();
        // Enter has to be caught on the way down. A TextBox that accepts newlines handles Enter
        // in its own class handler, and class handlers run before anything attached in XAML —
        // so the composer turned Enter into a line break and the send command never heard about
        // it. The tunnel reaches this view before the box the key was typed into.
        AddHandler(KeyDownEvent, Composer_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ChatMessagesScroll.ScrollChanged += (_, _) => RefreshScrollToEnd();
        DataContextChanged += (_, _) => AttachViewModel();
        AttachedToVisualTree += (_, _) => AttachViewModel();
        DetachedFromVisualTree += (_, _) => DetachViewModel();
    }

    private void AttachViewModel()
    {
        if (ReferenceEquals(_viewModel, DataContext)) return;
        DetachViewModel();
        _viewModel = DataContext as EdgeShellViewModel;
        if (_viewModel is null) return;
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        _viewModel.Messages.CollectionChanged += Messages_OnCollectionChanged;
        _viewModel.Suggestions.CollectionChanged += Suggestions_OnCollectionChanged;
        _viewModel.CopyRequested += ViewModel_OnCopyRequested;
    }

    private void DetachViewModel()
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _viewModel.Messages.CollectionChanged -= Messages_OnCollectionChanged;
        _viewModel.Suggestions.CollectionChanged -= Suggestions_OnCollectionChanged;
        _viewModel.CopyRequested -= ViewModel_OnCopyRequested;
        _viewModel = null;
    }

    private void AccessPermissions_OnSelectionInvoked(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => AssistantAccessButton.Flyout?.Hide());

    /// <summary>
    /// The conversation is opened, or moved between the sheet and the widened window. Either
    /// way the view it lands in goes to the end of the thread and takes the caret.
    ///
    /// Answered by the copy the conversation has landed in — a hidden copy taking the caret is
    /// the caret leaving the window that is being typed in. The window it lands in may not be up
    /// yet at the moment the change is announced, so the caret is asked for after the layout
    /// that puts it there.
    /// </summary>
    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(EdgeShellViewModel.IsChatOpen) or nameof(EdgeShellViewModel.IsChatExpanded)
            or nameof(EdgeShellViewModel.ShowChatMain))) return;
        if (_viewModel is not { IsChatOpen: true, ShowChatMain: true } || !IsHostingTheConversation) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel?.ShowChatMain != true || !IsEffectivelyVisible || !IsHostingTheConversation) return;
            ChatMessagesScroll.ScrollToEnd();
            RefreshScrollToEnd();
            ComposerTextBox.Focus();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The clipboard lives on the top level, which only the view can reach. Answered by the
    /// copy that is on screen, so one press is one write rather than two.
    /// </summary>
    private async void ViewModel_OnCopyRequested(object? sender, string text)
    {
        if (!IsHostingTheConversation) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // A clipboard owned by another process can refuse; the message itself is unaffected.
        }
    }

    private void Messages_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(ScrollToNewest, DispatcherPriority.Background);

    /// <summary>
    /// Follows the thread, unless the reader has gone looking at something further up.
    ///
    /// An answer that is still being written lands here several times a second, and each one
    /// used to yank the view back to the bottom — so reading the paragraph above the one being
    /// typed was impossible for as long as the model kept typing. Once the reader leaves the
    /// end, the thread stops following and the button offering to take them back appears
    /// instead; reaching the end again puts them back in the stream.
    /// </summary>
    private void ScrollToNewest()
    {
        if (IsNearTheEnd()) ChatMessagesScroll.ScrollToEnd();
        RefreshScrollToEnd();
    }

    /// <summary>
    /// Within a screenful of the newest turn. Not "exactly at the end": a thread that is being
    /// written into grows under the reader, so an exact test would drop them out of the stream
    /// on the first token and never let them back in.
    /// </summary>
    private bool IsNearTheEnd()
    {
        var viewport = ChatMessagesScroll.Viewport.Height;
        var extent = ChatMessagesScroll.Extent.Height;
        if (extent <= viewport) return true;
        return extent - viewport - ChatMessagesScroll.Offset.Y <= Math.Max(72, viewport * 0.35);
    }

    private void RefreshScrollToEnd()
    {
        var wanted = !IsNearTheEnd() && _viewModel?.ShowChatMain == true;
        if (ScrollToEndButton.IsVisible != wanted) ScrollToEndButton.IsVisible = wanted;
    }

    private void ScrollToEnd_OnClick(object? sender, RoutedEventArgs e)
    {
        ChatMessagesScroll.ScrollToEnd();
        RefreshScrollToEnd();
    }

    private void Suggestions_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(ScrollToNewest, DispatcherPriority.Background);

    /// <summary>
    /// Enter sends, Shift+Enter breaks the line — in the composer and in a question being
    /// rewritten alike. Both boxes accept newlines of their own, so this is the only place the
    /// key can be answered before the editor swallows it.
    /// </summary>
    private async void Composer_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        var box = FocusedTextBox(e.Source);

        if (box is { DataContext: AssistantMessageViewModel message } editor &&
            editor.Classes.Contains("messageEditor"))
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                message.CancelEditCommand.Execute(null);
                ComposerTextBox.Focus();
                return;
            }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            await message.CommitEditCommand.ExecuteAsync(null);
            return;
        }

        if (e.Key != Key.Enter || !ReferenceEquals(box, ComposerTextBox) ||
            DataContext is not EdgeShellViewModel vm) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var caret = ComposerTextBox.CaretIndex;
            var text = ComposerTextBox.Text ?? string.Empty;
            var insertIndex = Math.Clamp(caret, 0, text.Length);
            ComposerTextBox.Text = text.Insert(insertIndex, Environment.NewLine);
            ComposerTextBox.CaretIndex = insertIndex + Environment.NewLine.Length;
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (vm.SubmitComposerCommand.CanExecute(null)) await vm.SubmitComposerCommand.ExecuteAsync(null);
    }

    /// <summary>The box a key was typed into, whether the event named it or a part of it.</summary>
    private static TextBox? FocusedTextBox(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox box) return box;
        return null;
    }

    /// <summary>
    /// The editor is put where the question was, so the pointer is already there and the
    /// keyboard should be too: it takes the caret with the old wording selected, because the
    /// usual reason for reopening a question is to ask a different one.
    ///
    /// Its first real size is the hook, not its arrival in the tree: the editor is built with
    /// the turn and only hidden, so it is attached long before anyone asks to edit anything.
    /// Growing from nothing is what being asked for looks like from here. Already holding the
    /// caret, it is being typed into and its size is only following the typing.
    /// </summary>
    private void MessageEditor_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // The other copy of the view lays the same editor out at the same moment, and it is not
        // the one being written in.
        if (!IsHostingTheConversation) return;
        if (sender is not TextBox { IsVisible: true, IsFocused: false } editor) return;
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        editor.Focus();
        editor.SelectAll();
        // The turn being rewritten is usually the last one and the list is usually resting at
        // its end, so the editor opening pushes the bubble it is in up past the top of the
        // viewport. Asked for once the new height has been laid out, not while it is arriving.
        Dispatcher.UIThread.Post(() => editor.BringIntoView(), DispatcherPriority.Background);
    }

    private async void RenameBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ChatConversationViewModel conversation }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await conversation.CommitRenameCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            conversation.CancelRenameCommand.Execute(null);
        }
    }

    private async void RenameBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ChatConversationViewModel { IsRenaming: true } conversation })
            await conversation.CommitRenameCommand.ExecuteAsync(null);
    }

    // The attach button carries its own Flyout, which Avalonia opens on click. Opening it from
    // a Click handler as well would toggle it straight back shut.
    private async void AttachImage_OnClick(object? sender, RoutedEventArgs e) => await PickAttachmentAsync(imagesOnly: true);
    private async void AttachDocument_OnClick(object? sender, RoutedEventArgs e) => await PickAttachmentAsync(imagesOnly: false);

    /// <summary>
    /// The dialog only offers formats the assistant can actually read. Letting any file through
    /// and failing later is what made attachments look broken.
    /// </summary>
    private async Task PickAttachmentAsync(bool imagesOnly)
    {
        if (DataContext is not EdgeShellViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            var images = new FilePickerFileType(vm.AttachImagesFilterName)
            {
                Patterns = AttachmentSupport.ImagePatterns,
                MimeTypes = ["image/*"]
            };
            var documents = new FilePickerFileType(vm.AttachDocumentsFilterName)
            {
                Patterns = AttachmentSupport.DocumentPatterns
            };

            var files = await ShellFilePicker.OpenFilesAsync(topLevel, new FilePickerOpenOptions
            {
                Title = vm.AttachDialogTitle,
                AllowMultiple = false,
                FileTypeFilter = imagesOnly ? [images, documents] : [documents, images]
            });
            if (files.Count > 0) vm.SetAttachedFile(System.IO.Path.GetFileName(files[0]), files[0]);
        }
        catch (Exception exception)
        {
            vm.ReportUnexpectedError(exception);
        }
    }

    private void CloseModelMenu_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is EdgeShellViewModel vm && vm.IsModelMenuOpen)
        {
            vm.CloseModelMenuCommand.Execute(null);
            e.Handled = true;
        }
    }
}
