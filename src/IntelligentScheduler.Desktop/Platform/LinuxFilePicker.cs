using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>A normal GTK/portal file chooser, outside the layer-shell window factory.</summary>
internal static class LinuxFilePicker
{
    private static Request? _pending;
    private static readonly Gtk.ResponseCallback Responded = (_, response, data) =>
    {
        var request = (Request)GCHandle.FromIntPtr(data).Target!;
        try { request.Respond(response); }
        catch (Exception exception) { request.Finish(exception: exception); }
    };

    public static Task<IReadOnlyList<string>> ShowAsync(TopLevel owner, int action, PickerOptions options,
        IReadOnlyList<FilePickerFileType>? types, bool multiple, string? suggestedName = null, string? extension = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_pending is not null) return Task.FromResult<IReadOnlyList<string>>([]);
        var request = new Request(owner, action, extension);
        try
        {
            request.Dialog = Gtk.gtk_file_chooser_native_new(options.Title, owner.PlatformImpl!.Handle!.Handle, action, null, null);
            if (request.Dialog == 0) throw new InvalidOperationException("The system file chooser could not be created.");
            Gtk.gtk_native_dialog_set_modal(request.Dialog, 1);
            Gtk.gtk_file_chooser_set_local_only(request.Dialog, 1);
            Gtk.gtk_file_chooser_set_select_multiple(request.Dialog, multiple ? 1 : 0);
            if (action == 1)
            {
                Gtk.gtk_file_chooser_set_do_overwrite_confirmation(request.Dialog, 1);
                if (!string.IsNullOrWhiteSpace(suggestedName))
                    Gtk.gtk_file_chooser_set_current_name(request.Dialog, Path.GetFileName(suggestedName));
            }
            if (options.SuggestedStartLocation?.TryGetLocalPath() is { } folder && Directory.Exists(folder))
                Gtk.gtk_file_chooser_set_current_folder(request.Dialog, folder);
            foreach (var type in types ?? [])
            {
                var filter = Gtk.gtk_file_filter_new();
                Gtk.gtk_file_filter_set_name(filter, type.Name);
                foreach (var pattern in type.Patterns ?? []) Gtk.gtk_file_filter_add_pattern(filter, pattern);
                foreach (var mime in type.MimeTypes ?? []) Gtk.gtk_file_filter_add_mime_type(filter, mime);
                Gtk.gtk_file_chooser_add_filter(request.Dialog, filter);
            }
            request.Handle = GCHandle.Alloc(request);
            request.Signal = Gtk.g_signal_connect_data(request.Dialog, "response", Responded, GCHandle.ToIntPtr(request.Handle), 0, 0);
            _pending = request;
            request.Layers = LinuxLayerShell.SuspendForFileDialog();
            Gtk.gtk_native_dialog_show(request.Dialog);
        }
        catch (Exception exception) { request.Finish(exception: exception); }
        return request.Completion.Task;
    }

    internal static string WithExtension(string path, string? extension)
        => string.IsNullOrEmpty(extension) || Path.HasExtension(path) ? path : path + "." + extension.TrimStart('.');

    private sealed class Request(TopLevel owner, int action, string? extension)
    {
        public readonly TaskCompletionSource<IReadOnlyList<string>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public nint Dialog;
        public ulong Signal;
        public GCHandle Handle;
        public IDisposable? Layers;

        public void Respond(int response)
        {
            if (response != -3) { Finish([]); return; } // GTK_RESPONSE_ACCEPT
            var names = Gtk.gtk_file_chooser_get_filenames(Dialog);
            var paths = new List<string>();
            try
            {
                for (var node = names; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    var text = Marshal.ReadIntPtr(node);
                    try { if (Marshal.PtrToStringUTF8(text) is { } path) paths.Add(path); }
                    finally { Gtk.g_free(text); }
                }
            }
            finally { Gtk.g_slist_free(names); }
            if (action == 1 && paths.Count > 0)
            {
                var resolved = WithExtension(paths[0], extension);
                if (resolved != paths[0] && File.Exists(resolved))
                {
                    // Let GTK confirm the actual destination, including the appended extension.
                    // A bare name must never bypass the native overwrite confirmation.
                    Gtk.gtk_file_chooser_set_current_name(Dialog, Path.GetFileName(resolved));
                    Dispatcher.UIThread.Post(() => { if (Dialog != 0) Gtk.gtk_native_dialog_show(Dialog); });
                    return;
                }
                paths[0] = resolved;
            }
            Finish(paths);
        }

        public void Finish(IReadOnlyList<string>? paths = null, Exception? exception = null)
        {
            if (Completion.Task.IsCompleted) return;
            if (Dialog != 0)
            {
                if (Signal != 0) Gtk.g_signal_handler_disconnect(Dialog, Signal);
                Gtk.gtk_native_dialog_destroy(Dialog);
                Gtk.g_object_unref(Dialog);
                Dialog = 0;
            }
            if (Handle.IsAllocated) Handle.Free();
            Layers?.Dispose();
            if (ReferenceEquals(_pending, this)) _pending = null;
            if (owner is Window { IsVisible: true } window) window.Activate();
            if (exception is not null) Completion.TrySetException(exception);
            else Completion.TrySetResult(paths ?? []);
        }
    }
}
