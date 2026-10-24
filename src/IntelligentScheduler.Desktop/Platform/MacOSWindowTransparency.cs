using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Removes the native rectangular backing from the borderless, alpha-composited windows used
/// by the shell. Avalonia renders the rounded surface and its subtle border itself; an NSWindow
/// shadow follows the native rectangular frame instead and shows through as a plate on macOS.
/// </summary>
internal static class MacOSWindowTransparency
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    public static void Prepare(params Window[] windows)
    {
        if (!OperatingSystem.IsMacOS()) return;
        foreach (var window in windows) window.Opened += ApplyAfterOpen;
    }

    public static void Refresh(params Window[] windows)
    {
        if (!OperatingSystem.IsMacOS()) return;
        foreach (var window in windows)
        {
            if (window.IsVisible) ScheduleRefresh(window);
        }
    }

    private static void ApplyAfterOpen(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        ScheduleRefresh(window);
    }

    private static void ScheduleRefresh(Window window)
    {
        ApplyNativeSurface(window);

        // Avalonia creates the macOS composition layer after Opened. The first assignment above
        // can therefore be overwritten as that layer is attached. Re-negotiate once on the next
        // render turn and once after Core Animation has committed its first frame — the same
        // transition that changing the transparency switch by hand used to trigger.
        Dispatcher.UIThread.Post(() => RefreshAfterCompositorStarts(window), DispatcherPriority.Render);
        _ = RefreshAfterFirstFrameAsync(window);
    }

    private static async Task RefreshAfterFirstFrameAsync(Window window)
    {
        try
        {
            await Task.Delay(180);
            await Dispatcher.UIThread.InvokeAsync(() => RefreshAfterCompositorStarts(window),
                DispatcherPriority.Background);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The application finished while the first-frame refresh was waiting.
        }
    }

    private static void RefreshAfterCompositorStarts(Window window)
    {
        if (!window.IsVisible) return;
        try
        {
            var desiredLevels = window.TransparencyLevelHint.ToArray();
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            window.TransparencyLevelHint = desiredLevels;
            ApplyNativeSurface(window);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The window was closed between the posted refresh and this dispatcher turn.
        }
    }

    private static void ApplyNativeSurface(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero ||
                !string.Equals(handle.HandleDescriptor, "NSWindow", StringComparison.OrdinalIgnoreCase))
                return;

            var nativeWindow = handle.Handle;
            SendBoolean(nativeWindow, Selector("setOpaque:"), 0);
            SendBoolean(nativeWindow, Selector("setHasShadow:"), 0);

            var nsColor = GetClass("NSColor");
            var clearColor = SendPointer(nsColor, Selector("clearColor"));
            if (clearColor != IntPtr.Zero)
                SendPointer(nativeWindow, Selector("setBackgroundColor:"), clearColor);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // A future macOS runtime may move the Objective-C shim. Avalonia's transparent
            // fallback remains active, so failure to remove the native shadow is non-fatal.
        }
    }

    private static IntPtr Selector(string name) => RegisterSelector(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBoolean(IntPtr receiver, IntPtr selector, byte value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendPointer(IntPtr receiver, IntPtr selector, IntPtr value);
}
