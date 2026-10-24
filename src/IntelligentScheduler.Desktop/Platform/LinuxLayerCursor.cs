using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace IntelligentScheduler.Desktop.Platform;

internal sealed class LinuxLayerCursor(nint handle) : ICursorImpl
{
    public nint Handle { get; private set; } = handle;
    public void Dispose()
    {
        if (Handle == 0) return;
        Gtk.g_object_unref(Handle);
        Handle = 0;
    }
}

internal sealed class LinuxLayerCursorFactory : ICursorFactory
{
    public ICursorImpl GetCursor(StandardCursorType type)
    {
        var name = type switch
        {
            StandardCursorType.Hand => "pointer",
            StandardCursorType.Ibeam => "text",
            StandardCursorType.Cross => "crosshair",
            StandardCursorType.No => "not-allowed",
            StandardCursorType.None => "none",
            StandardCursorType.Wait => "wait",
            StandardCursorType.AppStarting => "progress",
            StandardCursorType.Help => "help",
            StandardCursorType.SizeWestEast => "ew-resize",
            StandardCursorType.SizeNorthSouth => "ns-resize",
            StandardCursorType.SizeAll or StandardCursorType.DragMove => "move",
            StandardCursorType.DragCopy => "copy",
            StandardCursorType.DragLink => "alias",
            StandardCursorType.TopSide => "n-resize",
            StandardCursorType.BottomSide => "s-resize",
            StandardCursorType.LeftSide => "w-resize",
            StandardCursorType.RightSide => "e-resize",
            StandardCursorType.TopLeftCorner => "nw-resize",
            StandardCursorType.TopRightCorner => "ne-resize",
            StandardCursorType.BottomLeftCorner => "sw-resize",
            StandardCursorType.BottomRightCorner => "se-resize",
            _ => "default"
        };
        return new LinuxLayerCursor(Gtk.gdk_cursor_new_from_name(Gtk.gdk_display_get_default(), name));
    }

    public ICursorImpl CreateCursor(Bitmap bitmap, PixelPoint hotSpot)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        var bytes = stream.ToArray();
        var loader = Gtk.gdk_pixbuf_loader_new();
        try
        {
            if (Gtk.gdk_pixbuf_loader_write(loader, bytes, (nuint)bytes.Length, 0) == 0
                || Gtk.gdk_pixbuf_loader_close(loader, 0) == 0) return GetCursor(StandardCursorType.Arrow);
            return new LinuxLayerCursor(Gtk.gdk_cursor_new_from_pixbuf(Gtk.gdk_display_get_default(),
                Gtk.gdk_pixbuf_loader_get_pixbuf(loader), hotSpot.X, hotSpot.Y));
        }
        finally { Gtk.g_object_unref(loader); }
    }
}
