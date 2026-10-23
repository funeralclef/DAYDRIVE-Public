using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Text belongs to the GTK connection that receives Wayland keyboard focus and selection
/// serials. An unrelated Xwayland connection cannot publish the user's copied text reliably.
/// Other formats retain Avalonia's existing desktop implementation.
/// </summary>
internal sealed class LinuxLayerClipboard(IClipboard fallback) : IClipboard
{
    private nint Clipboard => Gtk.gtk_clipboard_get_for_display(Gtk.gdk_display_get_default(), Gtk.gdk_atom_intern("CLIPBOARD", 0));
    private static readonly Gtk.TextCallback Received = (_, text, data) =>
    {
        var handle = GCHandle.FromIntPtr(data);
        try { ((TaskCompletionSource<string?>)handle.Target!).TrySetResult(Marshal.PtrToStringUTF8(text)); }
        catch (Exception exception) { ((TaskCompletionSource<string?>)handle.Target!).TrySetException(exception); }
        finally { handle.Free(); }
    };

    public async Task<IAsyncDataTransfer?> TryGetDataAsync()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Gtk.gtk_clipboard_request_text(Clipboard, Received, GCHandle.ToIntPtr(GCHandle.Alloc(completion)));
        if (await completion.Task is not { } text) return await fallback.TryGetDataAsync();
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        return transfer;
    }

    public async Task SetDataAsync(IAsyncDataTransfer? data)
    {
        if (data is null) { await ClearAsync(); return; }
        if (await data.TryGetTextAsync() is not { } text) { await fallback.SetDataAsync(data); return; }
        try { Gtk.gtk_clipboard_set_text(Clipboard, text, -1); }
        finally { data.Dispose(); }
    }

    public Task ClearAsync() { Gtk.gtk_clipboard_clear(Clipboard); return Task.CompletedTask; }
    public Task FlushAsync() { Gtk.gtk_clipboard_store(Clipboard); return Task.CompletedTask; }
    public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => Task.FromResult<IAsyncDataTransfer?>(null);
}
