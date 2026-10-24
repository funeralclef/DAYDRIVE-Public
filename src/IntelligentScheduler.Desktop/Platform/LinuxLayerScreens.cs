using Avalonia;
using Avalonia.Platform;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>GDK monitor geometry uses logical desktop coordinates, independent of buffer DPI.</summary>
internal sealed class LinuxLayerScreens : ScreensBase<nint, LinuxLayerScreens.LayerScreen>
{
    public static LinuxLayerScreens Instance { get; } = new();
    private readonly List<Delegate> _callbacks = [];

    private LinuxLayerScreens()
    {
        var display = Gtk.gdk_display_get_default();
        foreach (var signal in new[] { "monitor-added", "monitor-removed" })
        {
            var callback = new Gtk.TextCallback((_, _, _) => OnChanged());
            _callbacks.Add(callback);
            Gtk.g_signal_connect_data(display, signal, callback, 0, 0, 0);
        }
    }

    protected override IReadOnlyList<nint> GetAllScreenKeys()
    {
        var display = Gtk.gdk_display_get_default();
        return Enumerable.Range(0, Gtk.gdk_display_get_n_monitors(display))
            .Select(index => Gtk.gdk_display_get_monitor(display, index)).ToArray();
    }

    protected override LayerScreen CreateScreenFromKey(nint key) => new(key, this);
    protected override void ScreenChanged(LayerScreen screen) => screen.Refresh();
    protected override void ScreenRemoved(LayerScreen screen)
    {
        screen.Disconnect();
        base.ScreenRemoved(screen);
    }

    protected override Screen? ScreenFromTopLevelCore(ITopLevelImpl topLevel)
        => topLevel is LinuxLayerWindow window ? ScreenFromPoint(window.Position) ?? AllScreens.FirstOrDefault() : null;

    internal sealed class LayerScreen : PlatformScreen
    {
        public nint Monitor { get; }
        private readonly Gtk.TextCallback _changed;
        private readonly ulong _handler;

        public LayerScreen(nint monitor, LinuxLayerScreens owner) : base(new PlatformHandle(monitor, "GdkMonitor"))
        {
            Monitor = Gtk.g_object_ref(monitor);
            _changed = (_, _, _) => owner.OnChanged();
            _handler = Gtk.g_signal_connect_data(monitor, "notify", _changed, 0, 0, 0);
            Refresh();
        }

        public void Disconnect()
        {
            Gtk.g_signal_handler_disconnect(Monitor, _handler);
            Gtk.g_object_unref(Monitor);
        }

        public void Refresh()
        {
            Gtk.gdk_monitor_get_geometry(Monitor, out var bounds);
            Gtk.gdk_monitor_get_workarea(Monitor, out var work);
            Bounds = new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            WorkingArea = new PixelRect(work.X, work.Y, work.Width, work.Height);
            Scaling = 1;
            var primary = Gtk.gdk_display_get_primary_monitor(Gtk.gdk_display_get_default());
            IsPrimary = primary == Monitor || primary == 0 && Gtk.gdk_display_get_monitor(Gtk.gdk_display_get_default(), 0) == Monitor;
        }
    }
}
