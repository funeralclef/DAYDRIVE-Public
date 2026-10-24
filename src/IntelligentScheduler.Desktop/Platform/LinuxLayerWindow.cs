using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// A software-rendered Avalonia window hosted by GTK layer-shell, rather than xdg-toplevel.
/// The application keeps its existing views, animation, positioning and hide/reveal logic.
/// Native GTK owns Wayland input, composition and buffer submission; Skia draws the UI.
/// </summary>
internal sealed class LinuxLayerWindow : IWindowImpl, IFramebufferPlatformSurface, ITextInputMethodImpl
{
    private nint _widget;
    private readonly nint _ime;
    private readonly List<Delegate> _callbacks = [];
    private readonly MouseDevice _mouse = new();
    private readonly IKeyboardDevice _keyboard = AvaloniaLocator.Current.GetRequiredService<IKeyboardDevice>();
    private readonly object _frameGate = new();
    private readonly Compositor _compositor = new(null);
    private IInputRoot? _inputRoot;
    private nint _pixels;
    private PixelSize _frameSize;
    private double _frameScale = 1;
    private nint _monitor;
    private bool _disposed;
    private bool _enabled = true;
    private bool _acceptFocus = true;
    private bool _active;
    private bool _visible;
    private bool _showQueued;
    private static readonly Gtk.IdleCallback ShowNative = data =>
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            var window = (LinuxLayerWindow)handle.Target!;
            window._showQueued = false;
            if (!window._disposed && window._visible) Guard(() =>
            {
                Gtk.gtk_widget_show(window._widget);
                Gtk.gtk_im_context_set_client_window(window._ime, Gtk.gtk_widget_get_window(window._widget));
                window.UpdateInputRegion();
                window.Paint?.Invoke(new Rect(window._clientSize));
            });
        }
        finally { handle.Free(); }
        return 0;
    };
    private bool _restoreOnDemand;
    private TextInputMethodClient? _textClient;
    private int _drawPending;
    private double _renderScale = 1;
    private Thickness _inputInsets;
    private Size _clientSize = new(1, 1);

    public LinuxLayerWindow()
    {
        _widget = Gtk.gtk_window_new(0);
        Gtk.gtk_window_set_decorated(_widget, 0);
        Gtk.gtk_window_set_resizable(_widget, 0);
        Gtk.gtk_widget_set_app_paintable(_widget, 1);
        var visual = Gtk.gdk_screen_get_rgba_visual(Gtk.gtk_widget_get_screen(_widget));
        if (visual != 0) Gtk.gtk_widget_set_visual(_widget, visual);
        Gtk.gtk_layer_init_for_window(_widget);
        Gtk.gtk_layer_set_namespace(_widget, "daydrive");
        Gtk.gtk_layer_set_layer(_widget, LinuxLayerShell.HasFileDialog ? 1 : 2);
        Gtk.gtk_layer_set_exclusive_zone(_widget, 0); // overlay, never reserve desktop space
        Gtk.gtk_layer_set_anchor(_widget, 1, 1); // right
        Gtk.gtk_layer_set_anchor(_widget, 2, 1); // top
        Gtk.gtk_layer_set_keyboard_mode(_widget, 2); // on demand, never exclusive
        Gtk.gtk_window_set_focus_on_map(_widget, 0);
        Gtk.gtk_widget_add_events(_widget, 0x3ffffe | (1 << 23)); // include GDK_SMOOTH_SCROLL_MASK
        Connect("draw", new Gtk.EventCallback(Draw));
        Connect("event", new Gtk.EventCallback(Event));
        Connect("delete-event", new Gtk.EventCallback((_, _, _) => Guard(() =>
        {
            if (!(Closing?.Invoke(WindowCloseReason.WindowClosing) ?? false)) Dispose();
        })));
        Connect("size-allocate", new Gtk.AllocateCallback(Allocated));
        _ime = Gtk.gtk_im_multicontext_new();
        var commit = new Gtk.TextCallback((_, text, _) => Guard(() =>
        {
            _textClient?.SetPreeditText(null);
            if (_inputRoot is { } root && _textClient is not null && Marshal.PtrToStringUTF8(text) is { Length: > 0 } value)
                Input?.Invoke(new RawTextInputEventArgs(_keyboard, 0, root, value));
        }));
        _callbacks.Add(commit);
        Gtk.g_signal_connect_data(_ime, "commit", commit, 0, 0, 0);
        var preedit = new Gtk.VoidCallback((_, _) => Guard(() =>
        {
            Gtk.gtk_im_context_get_preedit_string(_ime, out var text, out var attributes, out var cursor);
            try { _textClient?.SetPreeditText(Marshal.PtrToStringUTF8(text), cursor); }
            finally
            {
                if (text != 0) Gtk.g_free(text);
                if (attributes != 0) Gtk.pango_attr_list_unref(attributes);
            }
        }));
        _callbacks.Add(preedit);
        Gtk.g_signal_connect_data(_ime, "preedit-changed", preedit, 0, 0, 0);
        Move(default);
        LinuxLayerShell.Windows.Add(this);
    }

    private void Connect(string signal, Delegate callback)
    {
        _callbacks.Add(callback);
        Gtk.g_signal_connect_data(_widget, signal, callback, 0, 0, 0);
    }

    private static int Guard(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            // Do not unwind through native GTK frames. Use the same diagnostic file as the app.
            if (LinuxLayerShell.ReportError is { } report) report(exception);
            else System.Diagnostics.Trace.TraceError("DAYDRIVE layer surface: {0}", exception);
        }
        return 1;
    }

    public double DesktopScaling => 1; // GTK's desktop coordinates are logical, even at HiDPI.
    public double RenderScaling => _renderScale;
    public IPlatformHandle? Handle => _widget == 0 ? null : new PlatformHandle(_widget, "GtkWindow");
    public Size ClientSize => _clientSize;
    public Size? FrameSize => _clientSize;
    public PixelPoint Position { get; private set; }
    internal void SetFileDialogLayer(bool belowDialog)
    {
        if (_widget != 0) Gtk.gtk_layer_set_layer(_widget, belowDialog ? 1 : 2);
    }
    public Compositor Compositor => _compositor;
    public IPlatformRenderSurface[] Surfaces => [this];
    public WindowTransparencyLevel TransparencyLevel => WindowTransparencyLevel.Transparent;
    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1, 1, 1);
    public Size MaxAutoSizeHint => LinuxLayerScreens.Instance.ScreenFromWindow(this) is { } screen
        ? new Size(screen.WorkingArea.Width, screen.WorkingArea.Height) : new Size(1920, 1080);
    public WindowState WindowState { get; set; }
    public bool WindowStateGetterIsUsable => true;
    public Action<RawInputEventArgs>? Input { get; set; }
    public Action<Rect>? Paint { get; set; }
    public Action<Size, WindowResizeReason>? Resized { get; set; }
    public Action<double>? ScalingChanged { get; set; }
    public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
    public Action? Closed { get; set; }
    public Action? LostFocus { get; set; }
    public Action<PixelPoint>? PositionChanged { get; set; }
    public Action? Activated { get; set; }
    public Action? Deactivated { get; set; }
    public Action<WindowState>? WindowStateChanged { get; set; }
    public Action? GotInputWhenDisabled { get; set; }
    public Func<WindowCloseReason, bool>? Closing { get; set; }
    public bool IsClientAreaExtendedToDecorations => true;
    public bool NeedsManagedDecorations => false;
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations => PlatformRequestedDrawnDecoration.None;
    public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
    public Thickness ExtendedMargins => default;
    public Thickness OffScreenMargin => default;

    public object? TryGetFeature(Type type) => type == typeof(IScreenImpl) ? LinuxLayerScreens.Instance
        : type == typeof(ITextInputMethodImpl) ? this
        : type == typeof(IClipboard) ? AvaloniaLocator.Current.GetRequiredService<IClipboard>() : null;
    public void SetClient(TextInputMethodClient? client)
    {
        Reset();
        _textClient = client;
        Gtk.gtk_im_context_set_use_preedit(_ime, client?.SupportsPreedit == true ? 1 : 0);
    }
    public void SetCursorRect(Rect rect)
    {
        var location = new Gtk.Rectangle
        {
            X = (int)rect.X,
            Y = (int)rect.Y,
            Width = Math.Max(1, (int)rect.Width),
            Height = Math.Max(1, (int)rect.Height)
        };
        Gtk.gtk_im_context_set_cursor_location(_ime, ref location);
    }
    public void SetOptions(TextInputOptions options) { }
    public void Reset() { Gtk.gtk_im_context_reset(_ime); _textClient?.SetPreeditText(null); }
    public void SetInputRoot(IInputRoot root) => _inputRoot = root;
    public Point PointToClient(PixelPoint point) => new(point.X - Position.X, point.Y - Position.Y);
    public PixelPoint PointToScreen(Point point) => new(Position.X + (int)Math.Round(point.X), Position.Y + (int)Math.Round(point.Y));
    public IPopupImpl? CreatePopup() => null; // Avalonia embeds menus, tooltips and combos in the layer.
    public void SetFrameThemeVariant(PlatformThemeVariant? variant) { }
    public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> levels)
        => TransparencyLevelChanged?.Invoke(TransparencyLevel);
    public void SetTitle(string? title) => Gtk.gtk_window_set_title(_widget, title ?? "DAYDRIVE");
    public void SetParent(IWindowImpl? parent) { }
    public void SetEnabled(bool enabled) { _enabled = enabled; Gtk.gtk_widget_set_sensitive(_widget, enabled ? 1 : 0); }
    public void SetWindowDecorations(WindowDecorations decorations) { }
    public void SetIcon(IWindowIconImpl? icon) { }
    public void ShowTaskbarIcon(bool value) { }
    public void CanResize(bool value) { }
    public void SetCanMinimize(bool value) { }
    public void SetCanMaximize(bool value) { }
    public void SetMinMaxSize(Size minSize, Size maxSize) { }
    public void SetExtendClientAreaToDecorationsHint(bool value) { }
    public void SetExtendClientAreaTitleBarHeightHint(double height) { }
    public void BeginMoveDrag(PointerPressedEventArgs e) { }
    public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) { }
    public void SetTopmost(bool value) { } // Layer order belongs to the compositor, not X11.

    /// <summary>Passive surfaces must never acquire keyboard focus, even when clicked.</summary>
    public void SetAcceptFocus(bool value)
    {
        _acceptFocus = value;
        Gtk.gtk_window_set_accept_focus(_widget, value ? 1 : 0);
        Gtk.gtk_layer_set_keyboard_mode(_widget, value ? 2 : 0);
    }

    public void SetInputInset(double inset) => SetInputInsets(new Thickness(inset));

    public void SetInputInsets(Thickness insets)
    {
        _inputInsets = new Thickness(Math.Max(0, insets.Left), Math.Max(0, insets.Top),
            Math.Max(0, insets.Right), Math.Max(0, insets.Bottom));
        UpdateInputRegion();
    }

    private void UpdateInputRegion()
    {
        if (_widget == 0) return;
        var native = Gtk.gtk_widget_get_window(_widget);
        if (native == 0) return;
        var left = (int)Math.Round(_inputInsets.Left); var top = (int)Math.Round(_inputInsets.Top);
        var rectangle = new Gtk.Rectangle { X = left, Y = top,
            Width = Math.Max(1, (int)Math.Ceiling(_clientSize.Width) - left - (int)Math.Round(_inputInsets.Right)),
            Height = Math.Max(1, (int)Math.Ceiling(_clientSize.Height) - top - (int)Math.Round(_inputInsets.Bottom)) };
        var region = Gtk.cairo_region_create_rectangle(ref rectangle);
        try { Gtk.gdk_window_input_shape_combine_region(native, region, 0, 0); }
        finally { Gtk.cairo_region_destroy(region); }
    }

    public void Show(bool activate, bool isDialog)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LinuxLayerWindow));
        _visible = true;
        // A GTK map may run a Wayland roundtrip. Enter it from a GLib idle source, outside
        // Avalonia's non-recursive dispatcher source and outside GDK's input callbacks.
        if (!_showQueued)
        {
            _showQueued = true;
            Gtk.g_idle_add(ShowNative, GCHandle.ToIntPtr(GCHandle.Alloc(this)));
        }
        if (activate) Activate();
        // On-demand focus is intentionally acquired by clicking the surface. Exclusive focus
        // would steal the keyboard from every other app for as long as the strip is visible.
    }

    public void Hide()
    {
        if (_widget == 0) return;
        _visible = false;
        Gtk.gtk_widget_hide(_widget);
        _restoreOnDemand = false;
        Gtk.gtk_layer_set_keyboard_mode(_widget, _acceptFocus ? 2 : 0);
        Focus(false);
    }

    public void Activate()
    {
        if (_disposed || !_acceptFocus || _active) return;
        // Only an explicit app reveal requests focus. Restore on-demand as soon as the
        // compositor confirms it, so this does not trap the user's keyboard in DAYDRIVE.
        _restoreOnDemand = true;
        Gtk.gtk_layer_set_keyboard_mode(_widget, 1);
        DispatcherTimer.RunOnce(() =>
        {
            if (_disposed || !_restoreOnDemand) return;
            _restoreOnDemand = false;
            Gtk.gtk_layer_set_keyboard_mode(_widget, 2);
        }, TimeSpan.FromMilliseconds(150));
    }

    public void Move(PixelPoint point)
    {
        if (_widget == 0) return;
        var screen = LinuxLayerScreens.Instance.ScreenFromPoint(point) ?? LinuxLayerScreens.Instance.AllScreens.FirstOrDefault();
        if (screen is not LinuxLayerScreens.LayerScreen target) return;
        if (_monitor != target.Monitor)
        {
            _monitor = target.Monitor;
            Gtk.gtk_layer_set_monitor(_widget, _monitor);
        }
        var scale = Gtk.gdk_monitor_get_scale_factor(_monitor);
        var changed = _renderScale != Math.Max(1, scale);
        lock (_frameGate) _renderScale = Math.Max(1, scale);
        // GTK's Wayland monitor work area may omit bars' exclusive zones. Let layer-shell
        // center ordinary panels in the compositor's available area, instead of adding a
        // guessed top margin to that area. This also keeps widgets level with their petals.
        var centered = _clientSize.Height <= target.WorkingArea.Height
            && Math.Abs(point.Y - (target.WorkingArea.Y + (target.WorkingArea.Height - _clientSize.Height) / 2)) <= 1;
        Gtk.gtk_layer_set_anchor(_widget, 2, centered ? 0 : 1);
        // The widget's shadow extends beyond the output while its card stays aligned with
        // the planner. Negative layer margins preserve that placement instead of shifting it.
        Gtk.gtk_layer_set_margin(_widget, 1, target.WorkingArea.Right - point.X - (int)Math.Ceiling(_clientSize.Width));
        Gtk.gtk_layer_set_margin(_widget, 2, centered ? 0 : Math.Max(0, point.Y - target.WorkingArea.Y));
        if (Position != point) { Position = point; PositionChanged?.Invoke(point); }
        if (changed) ScalingChanged?.Invoke(_renderScale);
    }

    public void Resize(Size size, WindowResizeReason reason = WindowResizeReason.Application)
    {
        if (_widget == 0 || size == _clientSize) return;
        var rounded = new Size(Math.Max(1, Math.Ceiling(size.Width)), Math.Max(1, Math.Ceiling(size.Height)));
        lock (_frameGate) _clientSize = rounded;
        Gtk.gtk_widget_set_size_request(_widget, (int)rounded.Width, (int)rounded.Height);
        Gtk.gtk_window_resize(_widget, 1, 1);
        UpdateInputRegion();
        Move(Position);
        Resized?.Invoke(rounded, reason);
    }

    private void Allocated(nint widget, ref Gtk.Rectangle rect, nint user)
    {
        var size = new Size(rect.Width, rect.Height);
        Guard(() =>
        {
            if (size.Width <= 0 || size.Height <= 0 || size == _clientSize) return;
            lock (_frameGate) _clientSize = size;
            UpdateInputRegion();
            Move(Position);
            Resized?.Invoke(size, WindowResizeReason.Layout);
        });
    }

    private void Focus(bool active)
    {
        if (active == _active) return;
        _active = active;
        if (active && _restoreOnDemand)
        {
            _restoreOnDemand = false;
            Gtk.gtk_layer_set_keyboard_mode(_widget, 2);
        }
        if (active) { Gtk.gtk_im_context_focus_in(_ime); Activated?.Invoke(); }
        else { Gtk.gtk_im_context_focus_out(_ime); LostFocus?.Invoke(); Deactivated?.Invoke(); }
    }

    private int Event(nint widget, nint ev, nint user)
    {
        var eventType = Gtk.gdk_event_get_event_type(ev);
        // GTK must process configure/map events itself. Consuming them here prevents a
        // remapped layer surface from completing its configure handshake.
        if (eventType is not (3 or 4 or 7 or 8 or 9 or 10 or 11 or 12 or 31)) return 0;
        var result = Guard(() =>
        {
            if (_inputRoot is not { } root) return;
            if (!_enabled) { GotInputWhenDisabled?.Invoke(); return; }
            var type = eventType;
            var timestamp = Gtk.gdk_event_get_time(ev);
            Gtk.gdk_event_get_state(ev, out var state);
            var modifiers = Modifiers(state);
            Gtk.gdk_event_get_coords(ev, out var x, out var y);
            var point = new Point(x, y);
            switch (type)
            {
                case 3: Pointer(RawPointerEventType.Move); break;
                case 4:
                case 7:
                    Gtk.gdk_event_get_button(ev, out var button);
                    if (button is < 1 or > 3) break;
                    var down = type == 4;
                    var flag = button switch
                    {
                        1 => RawInputModifiers.LeftMouseButton,
                        2 => RawInputModifiers.MiddleMouseButton,
                        _ => RawInputModifiers.RightMouseButton
                    };
                    modifiers = down ? modifiers | flag : modifiers & ~flag;
                    Pointer((button, down) switch
                    {
                        (1, true) => RawPointerEventType.LeftButtonDown,
                        (1, false) => RawPointerEventType.LeftButtonUp,
                        (2, true) => RawPointerEventType.MiddleButtonDown,
                        (2, false) => RawPointerEventType.MiddleButtonUp,
                        (3, true) => RawPointerEventType.RightButtonDown,
                        _ => RawPointerEventType.RightButtonUp
                    });
                    break;
                case 8:
                case 9:
                    Gtk.gdk_event_get_keyval(ev, out var keyval);
                    Gtk.gdk_event_get_keycode(ev, out var code);
                    var unicode = Gtk.gdk_keyval_to_unicode(keyval);
                    var symbol = unicode is >= 32 and <= 0x10ffff ? char.ConvertFromUtf32((int)unicode) : null;
                    var key = LinuxLayerKeys.Translate(keyval, code);
                    var args = new RawKeyEventArgs(_keyboard, timestamp, root,
                        type == 8 ? RawKeyEventType.KeyDown : RawKeyEventType.KeyUp, key, modifiers, PhysicalKey.None, symbol);
                    Input?.Invoke(args);
                    if (!args.Handled && _acceptFocus && _textClient is not null) Gtk.gtk_im_context_filter_keypress(_ime, ev);
                    break;
                case 10: Pointer(RawPointerEventType.Move); break;
                case 11: Pointer(RawPointerEventType.LeaveWindow); break;
                case 12:
                    // GdkEventFocus.in is a gint16 after type, window and send_event (GDK 3 ABI).
                    Focus(Marshal.ReadInt16(ev, IntPtr.Size == 8 ? 18 : 10) != 0);
                    break;
                case 31:
                    var delta = LinuxLayerInput.ScrollDelta(ev);
                    if (Environment.GetEnvironmentVariable("DAYDRIVE_LAYER_INPUT_TRACE") == "1")
                        Console.Error.WriteLine($"Layer scroll {delta}");
                    if (delta != default)
                        Input?.Invoke(new RawMouseWheelEventArgs(_mouse, timestamp, root, point, delta, modifiers));
                    break;
            }
            void Pointer(RawPointerEventType kind)
            {
                var input = new RawPointerEventArgs(_mouse, timestamp, root, kind, point, modifiers);
                Input?.Invoke(input);
                if (Environment.GetEnvironmentVariable("DAYDRIVE_LAYER_INPUT_TRACE") == "1" && kind is not RawPointerEventType.Move)
                    Console.Error.WriteLine($"Layer input {kind} at {point}, {modifiers}, handled={input.Handled}");
            }
        });
        return eventType == 12 ? 0 : result;
    }

    private static RawInputModifiers Modifiers(uint state)
    {
        var result = RawInputModifiers.None;
        if ((state & 1) != 0) result |= RawInputModifiers.Shift;
        if ((state & 4) != 0) result |= RawInputModifiers.Control;
        if ((state & 8) != 0) result |= RawInputModifiers.Alt;
        if ((state & (1 << 26)) != 0) result |= RawInputModifiers.Meta;
        if ((state & (1 << 8)) != 0) result |= RawInputModifiers.LeftMouseButton;
        if ((state & (1 << 9)) != 0) result |= RawInputModifiers.MiddleMouseButton;
        if ((state & (1 << 10)) != 0) result |= RawInputModifiers.RightMouseButton;
        return result;
    }

    public void SetCursor(ICursorImpl? cursor)
    {
        var window = _widget == 0 ? 0 : Gtk.gtk_widget_get_window(_widget);
        if (window != 0) Gtk.gdk_window_set_cursor(window, (cursor as LinuxLayerCursor)?.Handle ?? 0);
    }

    public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new FuncFramebufferRenderTarget(LockFrame);

    private ILockedFramebuffer LockFrame()
    {
        Monitor.Enter(_frameGate);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(_clientSize.Width * _renderScale)),
                Math.Max(1, (int)Math.Ceiling(_clientSize.Height * _renderScale)));
            if (_pixels == 0 || size != _frameSize)
            {
                if (_pixels != 0) Marshal.FreeHGlobal(_pixels);
                _pixels = Marshal.AllocHGlobal(checked(size.Width * size.Height * 4));
                _frameSize = size;
            }
            _frameScale = _renderScale;
            return new Frame(this, _pixels, size, _renderScale);
        }
        catch { Monitor.Exit(_frameGate); throw; }
    }

    private sealed class Frame(LinuxLayerWindow owner, nint address, PixelSize size, double scale) : ILockedFramebuffer
    {
        public nint Address => address;
        public PixelSize Size => size;
        public int RowBytes => size.Width * 4;
        public Vector Dpi => new(96 * scale, 96 * scale);
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Premul;
        public void Dispose()
        {
            Monitor.Exit(owner._frameGate);
            if (Interlocked.Exchange(ref owner._drawPending, 1) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref owner._drawPending, 0);
                if (!owner._disposed) Gtk.gtk_widget_queue_draw(owner._widget);
            }, DispatcherPriority.Render);
        }
    }

    private int Draw(nint widget, nint cairo, nint user) => Guard(() =>
    {
        lock (_frameGate)
        {
            if (_pixels == 0 || _disposed) return;
            var surface = Gtk.cairo_image_surface_create_for_data(_pixels, 0, _frameSize.Width, _frameSize.Height, _frameSize.Width * 4);
            try
            {
                Gtk.cairo_scale(cairo, 1 / _frameScale, 1 / _frameScale);
                Gtk.cairo_set_operator(cairo, 1); // SOURCE preserves transparent rounded corners.
                Gtk.cairo_set_source_surface(cairo, surface, 0, 0);
                Gtk.cairo_paint(cairo);
            }
            finally { Gtk.cairo_surface_destroy(surface); }
        }
    });

    public void Dispose()
    {
        if (_disposed) return;
        LinuxLayerShell.Windows.Remove(this);
        lock (_frameGate)
        {
            _disposed = true;
            if (_pixels != 0) { Marshal.FreeHGlobal(_pixels); _pixels = 0; }
        }
        Gtk.gtk_im_context_set_client_window(_ime, 0);
        Gtk.g_object_unref(_ime);
        Gtk.gtk_widget_destroy(_widget);
        _widget = 0;
        _callbacks.Clear();
        _mouse.Dispose();
        Closed?.Invoke();
    }
}
