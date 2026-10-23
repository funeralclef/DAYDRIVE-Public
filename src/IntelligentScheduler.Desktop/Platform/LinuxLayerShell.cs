using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Hosts the existing edge windows on native Wayland layer surfaces. Xwayland is still used
/// by Avalonia for desktop services (clipboard, portals and tray), but never for placement.
/// GTK and Avalonia share the GLib main loop; all GTK calls belong to the UI thread.
/// </summary>
internal static class LinuxLayerShell
{
    public static bool IsSupported { get; private set; }
    public static bool UsesGtk { get; private set; }
    public static string UnavailableReason { get; private set; } = "not initialized";
    public static Action<Exception>? ReportError { get; set; }

    public static void Initialize()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return;
        if (Environment.GetEnvironmentVariable("DAYDRIVE_LAYER_SHELL") == "0")
        {
            UnavailableReason = "disabled by DAYDRIVE_LAYER_SHELL=0";
            return;
        }
        try
        {
            Gtk.gdk_set_allowed_backends("wayland");
            if (Gtk.gtk_init_check(0, 0) == 0)
            {
                UnavailableReason = "GTK could not connect to Wayland";
                return;
            }
            UsesGtk = true;
            if (Gtk.gtk_layer_is_supported() == 0 || Gtk.gtk_layer_get_protocol_version() < 4)
            {
                UnavailableReason = "the compositor does not provide layer-shell v4 (on-demand keyboard focus)";
                return;
            }
            IsSupported = true;
            UnavailableReason = "";
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            UnavailableReason = "GTK 3 or gtk-layer-shell is missing or too old";
        }
    }

    public static void Install()
    {
        if (!IsSupported || ShellEnvironment.Mode != Application.DesktopShellMode.Edge) return;
        var original = AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>();
        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>().ToConstant(new LayerPlatform(original));
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new LinuxLayerCursorFactory());
        var clipboard = AvaloniaLocator.Current.GetRequiredService<IClipboard>();
        AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(new LinuxLayerClipboard(clipboard));
    }

    private sealed class LayerPlatform(IWindowingPlatform original) : IWindowingPlatform
    {
        public IWindowImpl CreateWindow() => new LinuxLayerWindow();
        public ITopLevelImpl CreateEmbeddableTopLevel() => original.CreateEmbeddableTopLevel();
        public IWindowImpl CreateEmbeddableWindow() => original.CreateEmbeddableWindow();
        public ITrayIconImpl? CreateTrayIcon() => original.CreateTrayIcon();
        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder) => zOrder.Clear();
    }
}

// GTK's gboolean is a 32-bit integer, including in callbacks. Keep delegates rooted for the
// entire native object's lifetime; never allow a managed exception across a GObject callback.
internal static class Gtk
{
    private const string GtkLib = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string Layer = "libgtk-layer-shell.so.0";
    private const string Object = "libgobject-2.0.so.0";
    private const string Cairo = "libcairo.so.2";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int EventCallback(nint widget, nint data, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void AllocateCallback(nint widget, ref Rectangle allocation, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void TextCallback(nint context, nint text, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void VoidCallback(nint context, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int IdleCallback(nint data);
    [StructLayout(LayoutKind.Sequential)] public struct Rectangle { public int X, Y, Width, Height; }

    [DllImport(Gdk)] public static extern void gdk_set_allowed_backends(string backends);
    [DllImport(GtkLib)] public static extern int gtk_init_check(nint argc, nint argv);
    [DllImport(Layer)] public static extern int gtk_layer_is_supported();
    [DllImport(Layer)] public static extern uint gtk_layer_get_protocol_version();
    [DllImport(Layer)] public static extern void gtk_layer_init_for_window(nint window);
    [DllImport(Layer)] public static extern void gtk_layer_set_namespace(nint window, string name);
    [DllImport(Layer)] public static extern void gtk_layer_set_layer(nint window, int layer);
    [DllImport(Layer)] public static extern void gtk_layer_set_monitor(nint window, nint monitor);
    [DllImport(Layer)] public static extern void gtk_layer_set_anchor(nint window, int edge, int anchor);
    [DllImport(Layer)] public static extern void gtk_layer_set_margin(nint window, int edge, int margin);
    [DllImport(Layer)] public static extern void gtk_layer_set_exclusive_zone(nint window, int zone);
    [DllImport(Layer)] public static extern void gtk_layer_set_keyboard_mode(nint window, int mode);
    [DllImport(GtkLib)] public static extern nint gtk_window_new(int type);
    [DllImport(GtkLib)] public static extern void gtk_window_set_title(nint window, string title);
    [DllImport(GtkLib)] public static extern void gtk_window_set_decorated(nint window, int decorated);
    [DllImport(GtkLib)] public static extern void gtk_window_set_resizable(nint window, int resizable);
    [DllImport(GtkLib)] public static extern void gtk_window_resize(nint window, int width, int height);
    [DllImport(GtkLib)] public static extern void gtk_window_set_accept_focus(nint window, int focus);
    [DllImport(GtkLib)] public static extern void gtk_window_set_focus_on_map(nint window, int focus);
    [DllImport(GtkLib)] public static extern void gtk_widget_set_size_request(nint widget, int width, int height);
    [DllImport(GtkLib)] public static extern void gtk_widget_set_app_paintable(nint widget, int value);
    [DllImport(GtkLib)] public static extern void gtk_widget_set_visual(nint widget, nint visual);
    [DllImport(GtkLib)] public static extern nint gtk_widget_get_screen(nint widget);
    [DllImport(GtkLib)] public static extern nint gtk_widget_get_window(nint widget);
    [DllImport(GtkLib)] public static extern void gtk_widget_add_events(nint widget, int events);
    [DllImport(GtkLib)] public static extern void gtk_widget_show(nint widget);
    [DllImport(GtkLib)] public static extern void gtk_widget_hide(nint widget);
    [DllImport(GtkLib)] public static extern void gtk_widget_destroy(nint widget);
    [DllImport(GtkLib)] public static extern void gtk_widget_queue_draw(nint widget);
    [DllImport(GtkLib)] public static extern void gtk_widget_set_sensitive(nint widget, int sensitive);
    [DllImport(GtkLib)] public static extern nint gtk_im_multicontext_new();
    [DllImport(GtkLib)] public static extern void gtk_im_context_set_client_window(nint context, nint window);
    [DllImport(GtkLib)] public static extern int gtk_im_context_filter_keypress(nint context, nint ev);
    [DllImport(GtkLib)] public static extern void gtk_im_context_focus_in(nint context);
    [DllImport(GtkLib)] public static extern void gtk_im_context_focus_out(nint context);
    [DllImport(GtkLib)] public static extern void gtk_im_context_reset(nint context);
    [DllImport(GtkLib)] public static extern void gtk_im_context_set_use_preedit(nint context, int preedit);
    [DllImport(GtkLib)] public static extern void gtk_im_context_get_preedit_string(nint context, out nint text, out nint attributes, out int cursor);
    [DllImport(GtkLib)] public static extern void gtk_im_context_set_cursor_location(nint context, ref Rectangle rect);
    [DllImport(GtkLib)] public static extern nint gtk_clipboard_get_for_display(nint display, nint atom);
    [DllImport(GtkLib)] public static extern void gtk_clipboard_request_text(nint clipboard, TextCallback callback, nint data);
    [DllImport(GtkLib)] public static extern void gtk_clipboard_set_text(nint clipboard, string text, int length);
    [DllImport(GtkLib)] public static extern void gtk_clipboard_clear(nint clipboard);
    [DllImport(GtkLib)] public static extern void gtk_clipboard_store(nint clipboard);
    [DllImport(Object)] public static extern ulong g_signal_connect_data(nint instance, string signal, Delegate callback, nint data, nint destroy, int flags);
    [DllImport(Object)] public static extern void g_object_unref(nint instance);
    [DllImport(Object)] public static extern nint g_object_ref(nint instance);
    [DllImport(Object)] public static extern void g_signal_handler_disconnect(nint instance, ulong handler);
    [DllImport(Gdk)] public static extern nint gdk_screen_get_rgba_visual(nint screen);
    [DllImport(Gdk)] public static extern nint gdk_display_get_default();
    [DllImport(Gdk)] public static extern nint gdk_atom_intern(string name, int onlyIfExists);
    [DllImport(Gdk)] public static extern int gdk_display_get_n_monitors(nint display);
    [DllImport(Gdk)] public static extern nint gdk_display_get_monitor(nint display, int index);
    [DllImport(Gdk)] public static extern nint gdk_display_get_primary_monitor(nint display);
    [DllImport(Gdk)] public static extern void gdk_monitor_get_geometry(nint monitor, out Rectangle rect);
    [DllImport(Gdk)] public static extern void gdk_monitor_get_workarea(nint monitor, out Rectangle rect);
    [DllImport(Gdk)] public static extern int gdk_monitor_get_scale_factor(nint monitor);
    [DllImport(Gdk)] public static extern int gdk_event_get_event_type(nint ev);
    [DllImport(Gdk)] public static extern uint gdk_event_get_time(nint ev);
    [DllImport(Gdk)] public static extern int gdk_event_get_coords(nint ev, out double x, out double y);
    [DllImport(Gdk)] public static extern int gdk_event_get_state(nint ev, out uint state);
    [DllImport(Gdk)] public static extern int gdk_event_get_button(nint ev, out uint button);
    [DllImport(Gdk)] public static extern int gdk_event_get_scroll_direction(nint ev, out int direction);
    [DllImport(Gdk)] public static extern int gdk_event_get_pointer_emulated(nint ev);
    [DllImport(Gdk)] public static extern int gdk_event_get_scroll_deltas(nint ev, out double x, out double y);
    [DllImport(Gdk)] public static extern int gdk_event_get_keyval(nint ev, out uint key);
    [DllImport(Gdk)] public static extern int gdk_event_get_keycode(nint ev, out ushort code);
    [DllImport(Gdk)] public static extern uint gdk_keyval_to_unicode(uint key);
    [DllImport(Gdk)] public static extern nint gdk_cursor_new_from_name(nint display, string name);
    [DllImport(Gdk)] public static extern nint gdk_cursor_new_from_pixbuf(nint display, nint pixbuf, int x, int y);
    [DllImport(Gdk)] public static extern void gdk_window_set_cursor(nint window, nint cursor);
    [DllImport(Gdk)] public static extern void gdk_window_input_shape_combine_region(nint window, nint region, int x, int y);
    [DllImport(Cairo)] public static extern nint cairo_image_surface_create_for_data(nint pixels, int format, int width, int height, int stride);
    [DllImport(Cairo)] public static extern void cairo_surface_destroy(nint surface);
    [DllImport(Cairo)] public static extern void cairo_set_source_surface(nint cairo, nint surface, double x, double y);
    [DllImport(Cairo)] public static extern void cairo_set_operator(nint cairo, int op);
    [DllImport(Cairo)] public static extern void cairo_scale(nint cairo, double x, double y);
    [DllImport(Cairo)] public static extern void cairo_paint(nint cairo);
    [DllImport(Cairo)] public static extern nint cairo_region_create_rectangle(ref Rectangle rectangle);
    [DllImport(Cairo)] public static extern void cairo_region_destroy(nint region);
    [DllImport("libglib-2.0.so.0")] public static extern void g_free(nint memory);
    [DllImport("libglib-2.0.so.0")] public static extern uint g_idle_add(IdleCallback callback, nint data);
    [DllImport("libpango-1.0.so.0")] public static extern void pango_attr_list_unref(nint list);
    [DllImport("libgdk_pixbuf-2.0.so.0")] public static extern nint gdk_pixbuf_loader_new();
    [DllImport("libgdk_pixbuf-2.0.so.0")] public static extern int gdk_pixbuf_loader_write(nint loader, byte[] bytes, nuint count, nint error);
    [DllImport("libgdk_pixbuf-2.0.so.0")] public static extern int gdk_pixbuf_loader_close(nint loader, nint error);
    [DllImport("libgdk_pixbuf-2.0.so.0")] public static extern nint gdk_pixbuf_loader_get_pixbuf(nint loader);
}
