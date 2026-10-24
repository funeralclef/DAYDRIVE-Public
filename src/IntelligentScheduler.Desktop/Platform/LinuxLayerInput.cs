using Avalonia;

namespace IntelligentScheduler.Desktop.Platform;

internal static class LinuxLayerInput
{
    public static Vector ScrollDelta(nint ev)
    {
        // GDK leaves direction untouched for smooth events. In particular, their stop
        // event must not become a default GDK_SCROLL_UP and undo a downward wheel tick.
        if (Gtk.gdk_event_get_scroll_direction(ev, out var direction) != 0)
        {
            // Wayland GDK emits an emulated discrete event alongside the smooth event.
            // Forward the original once, otherwise every mouse-wheel notch scrolls twice.
            if (Gtk.gdk_event_get_pointer_emulated(ev) != 0) return default;
            return direction switch
            {
                0 => new Vector(0, 1),
                1 => new Vector(0, -1),
                2 => new Vector(1, 0),
                3 => new Vector(-1, 0),
                _ => default
            };
        }
        return Gtk.gdk_event_get_scroll_deltas(ev, out var x, out var y) != 0
            && double.IsFinite(x) && double.IsFinite(y) ? new Vector(-x, -y) : default;
    }
}
