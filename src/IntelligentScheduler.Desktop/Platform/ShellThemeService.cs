using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Desktop.Controls;
using Microsoft.Win32;
using SkiaSharp;

namespace IntelligentScheduler.Desktop.Platform;

public sealed class ShellThemeService(FontCatalogService fontCatalog) : IDisposable
{
    private readonly LinuxSystemAppearance? _linuxAppearance = OperatingSystem.IsLinux() ? new() : null;
    private AppSettings? _currentSettings;
    private bool _disposed;

    public ShellPalette Apply(AppSettings settings)
    {
        _currentSettings = settings;
        _linuxAppearance?.Watch(settings.ThemeColorSource is ThemeColorSource.SystemAccent or ThemeColorSource.Wallpaper,
            settings.ThemeColorSource == ThemeColorSource.Wallpaper, () => Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && _currentSettings is { } current) Apply(current);
            }));
        var profile = MarelianAppearance.Profile(settings).Normalize();
        var palette = ResolvePalette(settings, profile);
        var accent = palette.Accent;
        var visualPalette = BuildVisualPalette(settings.VisualPalette, palette);
        EventVisuals.ApplyPalette(visualPalette);
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            var intensity = settings.EnableTransparency ? Math.Clamp(settings.TransparencyIntensity, 0, 80) / 100d : 0d;
            static Color Layer(Color color, double amount, double influence)
                => WithAlpha(color, (byte)Math.Clamp(Math.Round(color.A * (1 - amount * influence)), 0, 255));
            Set(app, "ShellBackground", new SolidColorBrush(Layer(palette.Background, intensity, 0.40)));
            Set(app, "ShellSurface", new SolidColorBrush(Layer(palette.Surface, intensity, 0.22)));
            Set(app, "ShellSurfaceAlt", new SolidColorBrush(Layer(palette.SurfaceAlt, intensity, 0.18)));
            // The same colour at nothing, for controls that fade *to* ShellSurfaceAlt under the
            // pointer. Avalonia's Transparent is transparent white, so a brush transition that
            // starts there passes through half-alpha white and flashes on the way in.
            Set(app, "ShellSurfaceAltTransparent", new SolidColorBrush(WithAlpha(palette.SurfaceAlt, 0)));
            Set(app, "ShellPopup", new SolidColorBrush(Layer(palette.Surface, intensity, 0.15)));
            // The assistant sheet lies over the planner rather than over the desktop, so what it
            // lets through is the agenda. Nothing softens that layer any more — the sheet used to
            // sit over a blurred planner, and a window that loses focus wholesale every time the
            // assistant is opened is a worse trade than the glass was worth — so legibility is
            // the sheet's own job now. It keeps the shell's tint and a trace of depth, and stops
            // well short of letting a second column of readable text through: a conversation is
            // read, not glanced at. Fully solid once transparency is switched off.
            Set(app, "ShellChatPanel", new SolidColorBrush(Layer(palette.Surface, intensity, 0.04)));
            Set(app, "ShellChatField", new SolidColorBrush(Layer(palette.SurfaceAlt, intensity, 0.16)));
            Set(app, "ShellPopupBlur", new SolidColorBrush(Layer(palette.Surface, intensity, 0.16)));
            Set(app, "ShellTint", palette.Surface);
            Set(app, "ShellTintBackground", palette.Background);
            Set(app, "ShellText", new SolidColorBrush(palette.Text));
            Set(app, "ShellMuted", new SolidColorBrush(palette.Muted));
            accent = palette.Accent;
            Set(app, "ShellAccent", new SolidColorBrush(accent));
            Set(app, "ShellSecondary", new SolidColorBrush(palette.Secondary));
            Set(app, "ShellTertiary", new SolidColorBrush(palette.Tertiary));
            var iconPrimary = EnsureContrast(Mix(palette.Text, palette.Accent, 0.16), palette.Background, 4.5, palette.Text);
            var iconMuted = EnsureContrast(Mix(palette.Muted, palette.Accent, 0.14), palette.Background, 3.2, palette.Text);
            var onAccent = BestContrastingTint(palette.Accent, palette.Background, palette.Text);
            var disabledText = WithAlpha(palette.Text, 0x72);
            var disabledOnAccent = WithAlpha(onAccent, 0x8A);
            Set(app, "ShellIconPrimary", new SolidColorBrush(iconPrimary));
            Set(app, "ShellIconMuted", new SolidColorBrush(iconMuted));
            Set(app, "ShellIconOnAccent", new SolidColorBrush(onAccent));
            Set(app, "ShellOnAccent", new SolidColorBrush(onAccent));
            var comboBackground = new SolidColorBrush(palette.SurfaceAlt);
            var comboHover = new SolidColorBrush(palette.Surface);
            var comboPressed = new SolidColorBrush(Mix(palette.SurfaceAlt, accent, 0.16));
            var comboBorder = new SolidColorBrush(palette.Stroke);
            var comboPopup = new SolidColorBrush(palette.Surface);
            var comboSoft = new SolidColorBrush(WithAlpha(Mix(accent, palette.Background, palette.IsDark ? 0.70 : 0.80), 235));
            var transparent = new SolidColorBrush(Colors.Transparent);
            Set(app, "ComboBoxBackground", comboBackground);
            Set(app, "ComboBoxBackgroundPointerOver", comboHover);
            Set(app, "ComboBoxBackgroundPressed", comboPressed);
            Set(app, "ComboBoxBackgroundDisabled", comboBackground);
            Set(app, "ComboBoxBackgroundUnfocused", comboBackground);
            Set(app, "ComboBoxBackgroundFocused", comboBackground);
            Set(app, "ComboBoxForeground", new SolidColorBrush(palette.Text));
            Set(app, "ComboBoxForegroundUnfocused", new SolidColorBrush(palette.Text));
            Set(app, "ComboBoxForegroundDisabled", new SolidColorBrush(disabledText));
            Set(app, "ComboBoxForegroundFocused", new SolidColorBrush(palette.Text));
            Set(app, "ComboBoxForegroundFocusedPressed", new SolidColorBrush(palette.Text));
            Set(app, "ComboBoxPlaceHolderForeground", new SolidColorBrush(palette.Muted));
            Set(app, "ComboBoxBorderBrush", comboBorder);
            Set(app, "ComboBoxBorderBrushPointerOver", new SolidColorBrush(accent));
            Set(app, "ComboBoxBorderBrushPressed", new SolidColorBrush(accent));
            Set(app, "ComboBoxBorderBrushDisabled", comboBorder);
            Set(app, "ComboBoxDropDownGlyphForeground", new SolidColorBrush(iconMuted));
            Set(app, "ComboBoxDropDownGlyphForegroundFocused", new SolidColorBrush(accent));
            Set(app, "ComboBoxDropDownGlyphForegroundFocusedPressed", new SolidColorBrush(accent));
            Set(app, "ComboBoxDropDownBackground", comboPopup);
            Set(app, "ComboBoxDropDownBorderBrush", new SolidColorBrush(accent));
            Set(app, "ComboBoxItemForeground", new SolidColorBrush(palette.Text));
            Set(app, "ComboBoxItemForegroundDisabled", new SolidColorBrush(disabledText));
            Set(app, "ComboBoxItemForegroundPointerOver", new SolidColorBrush(accent));
            Set(app, "ComboBoxItemForegroundPressed", new SolidColorBrush(palette.Text));
            Set(app, "ComboBoxItemForegroundSelected", new SolidColorBrush(onAccent));
            Set(app, "ComboBoxItemForegroundSelectedUnfocused", new SolidColorBrush(onAccent));
            Set(app, "ComboBoxItemForegroundSelectedDisabled", new SolidColorBrush(disabledOnAccent));
            Set(app, "ComboBoxItemForegroundSelectedPointerOver", new SolidColorBrush(onAccent));
            Set(app, "ComboBoxItemForegroundSelectedPressed", new SolidColorBrush(onAccent));
            Set(app, "ComboBoxItemBackground", transparent);
            Set(app, "ComboBoxItemBackgroundPointerOver", comboSoft);
            Set(app, "ComboBoxItemBackgroundPressed", comboPressed);
            Set(app, "ComboBoxItemBackgroundSelected", new SolidColorBrush(accent));
            Set(app, "ComboBoxItemBackgroundSelectedUnfocused", new SolidColorBrush(accent));
            Set(app, "ComboBoxItemBackgroundSelectedPointerOver", new SolidColorBrush(Mix(accent, palette.IsDark ? Colors.White : Colors.Black, 0.08)));
            Set(app, "ComboBoxItemBackgroundSelectedPressed", new SolidColorBrush(Mix(accent, palette.IsDark ? Colors.Black : Colors.White, 0.12)));
            Set(app, "ComboBoxItemBorderBrush", transparent);
            Set(app, "ComboBoxItemBorderBrushPointerOver", new SolidColorBrush(accent));
            Set(app, "ComboBoxItemBorderBrushSelected", new SolidColorBrush(accent));

            // Fluent's nested checkbox states replace simple control styles. Override the
            // actual template resources so light accents always receive
            // a dark glyph and readable labels, including hover/pressed/disabled states.
            Set(app, "CheckBoxForegroundUnchecked", new SolidColorBrush(palette.Text));
            Set(app, "CheckBoxForegroundUncheckedPointerOver", new SolidColorBrush(accent));
            Set(app, "CheckBoxForegroundUncheckedPressed", new SolidColorBrush(palette.Text));
            Set(app, "CheckBoxForegroundUncheckedDisabled", new SolidColorBrush(disabledText));
            Set(app, "CheckBoxForegroundChecked", new SolidColorBrush(palette.Text));
            Set(app, "CheckBoxForegroundCheckedPointerOver", new SolidColorBrush(accent));
            Set(app, "CheckBoxForegroundCheckedPressed", new SolidColorBrush(palette.Text));
            Set(app, "CheckBoxForegroundCheckedDisabled", new SolidColorBrush(disabledText));
            Set(app, "CheckBoxCheckBackgroundFillUnchecked", new SolidColorBrush(palette.SurfaceAlt));
            Set(app, "CheckBoxCheckBackgroundFillUncheckedPointerOver", new SolidColorBrush(palette.Surface));
            Set(app, "CheckBoxCheckBackgroundFillUncheckedPressed", comboPressed);
            Set(app, "CheckBoxCheckBackgroundFillUncheckedDisabled", new SolidColorBrush(palette.SurfaceAlt));
            Set(app, "CheckBoxCheckBackgroundStrokeUnchecked", new SolidColorBrush(palette.Stroke));
            Set(app, "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", new SolidColorBrush(accent));
            Set(app, "CheckBoxCheckBackgroundStrokeUncheckedPressed", new SolidColorBrush(accent));
            Set(app, "CheckBoxCheckBackgroundStrokeUncheckedDisabled", new SolidColorBrush(palette.Stroke));
            Set(app, "CheckBoxCheckBackgroundFillChecked", new SolidColorBrush(accent));
            Set(app, "CheckBoxCheckBackgroundFillCheckedPointerOver", new SolidColorBrush(Mix(accent, palette.IsDark ? Colors.White : Colors.Black, 0.08)));
            Set(app, "CheckBoxCheckBackgroundFillCheckedPressed", new SolidColorBrush(Mix(accent, palette.IsDark ? Colors.Black : Colors.White, 0.12)));
            Set(app, "CheckBoxCheckBackgroundFillCheckedDisabled", new SolidColorBrush(WithAlpha(accent, 0x86)));
            Set(app, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", new SolidColorBrush(accent));
            Set(app, "CheckBoxCheckBackgroundStrokeCheckedPressed", new SolidColorBrush(accent));
            Set(app, "CheckBoxCheckBackgroundStrokeCheckedDisabled", new SolidColorBrush(WithAlpha(accent, 0x86)));
            Set(app, "CheckBoxCheckGlyphForegroundChecked", new SolidColorBrush(onAccent));
            Set(app, "CheckBoxCheckGlyphForegroundCheckedPointerOver", new SolidColorBrush(onAccent));
            Set(app, "CheckBoxCheckGlyphForegroundCheckedPressed", new SolidColorBrush(onAccent));
            Set(app, "CheckBoxCheckGlyphForegroundCheckedDisabled", new SolidColorBrush(disabledOnAccent));
            Set(app, "ShellAccentHover", new SolidColorBrush(Mix(accent, palette.IsDark ? Colors.White : Colors.Black, 0.12)));
            Set(app, "ShellAccentGlow", new SolidColorBrush(Mix(accent, Colors.White, palette.IsDark ? 0.20 : 0.14)));
            Set(app, "ShellAccentDeep", new SolidColorBrush(Mix(accent, palette.IsDark ? Colors.Black : Colors.White, 0.14)));
            Set(app, "ShellAccentTransparent", new SolidColorBrush(WithAlpha(accent, 0)));
            Set(app, "ShellAccentSoft", new SolidColorBrush(palette.Selection));
            Set(app, "ShellStroke", new SolidColorBrush(Layer(palette.Stroke, intensity, 0.30)));
            Set(app, "ShellStrokeTransparent", new SolidColorBrush(WithAlpha(palette.Stroke, 0)));
            var danger = palette.Danger;
            Set(app, "ShellDanger", new SolidColorBrush(danger));
            Set(app, "ShellDangerHover", new SolidColorBrush(Mix(danger, palette.IsDark ? Colors.White : Colors.Black, 0.10)));
            Set(app, "ShellDangerGlow", new SolidColorBrush(Mix(danger, Colors.White, 0.18)));
            Set(app, "ShellDangerDeep", new SolidColorBrush(Mix(danger, Colors.Black, 0.14)));
            Set(app, "ShellDangerTransparent", new SolidColorBrush(WithAlpha(danger, 0)));
            Set(app, "ShellSuccess", new SolidColorBrush(visualPalette.Success));
            Set(app, "ShellSuccessSoft", new SolidColorBrush(WithAlpha(visualPalette.Success, 0x38)));
            Set(app, "ShellWarning", new SolidColorBrush(visualPalette.Warning));
            // One resource per category, named after the enum, so adding a category is one line
            // in the enum rather than a line here as well.
            foreach (var category in CategoryPalette.Categories)
                Set(app, $"ShellCategory{category}", new SolidColorBrush(visualPalette.Categories[(int)category]));
            Set(app, "ShellFocus", new SolidColorBrush(visualPalette.Focus));
            var accentGradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Mix(palette.Accent, Colors.White, palette.IsDark ? 0.28 : 0.15), 0.0),
                    new GradientStop(palette.Accent, 0.55),
                    new GradientStop(Mix(palette.Accent, palette.IsDark ? Colors.White : Colors.Black, 0.12), 1.0)
                }
            };
            Set(app, "ShellAccentGradient", accentGradient);

            Set(app, "SystemAccentColor", palette.Accent);
            Set(app, "SystemAccentColorLight1", Mix(palette.Accent, Colors.White, 0.15));
            Set(app, "SystemAccentColorLight2", Mix(palette.Accent, Colors.White, 0.30));
            Set(app, "SystemAccentColorLight3", Mix(palette.Accent, Colors.White, 0.48));
            Set(app, "SystemAccentColorDark1", Mix(palette.Accent, Colors.Black, 0.14));
            Set(app, "SystemAccentColorDark2", Mix(palette.Accent, Colors.Black, 0.28));
            Set(app, "SystemAccentColorDark3", Mix(palette.Accent, Colors.Black, 0.44));
            var radii = (Window: profile.WindowRadius, Card: profile.CardRadius,
                Item: profile.ItemRadius, Control: profile.ControlRadius);
            Set(app, "ShellWindowRadius", new CornerRadius(radii.Window));
            Set(app, "ShellCardRadius", new CornerRadius(radii.Card));
            var innerCardRadius = Math.Max(0, radii.Card - 1);
            Set(app, "ShellCardInnerBottomRadius", new CornerRadius(0, 0, innerCardRadius, innerCardRadius));
            Set(app, "ShellItemRadius", new CornerRadius(radii.Item));
            Set(app, "ShellControlRadius", new CornerRadius(radii.Control));
            Set(app, "ShellPetalRadius", new CornerRadius(radii.Control == 0 ? 0 : radii.Control + 3, 0, 0, radii.Control == 0 ? 0 : radii.Control + 3));
            Set(app, "ShellFolderRadius", new CornerRadius(radii.Control == 0 ? 0 : radii.Control + 2, 0, 0, 0));
            Set(app, "ShellTopRadius", new CornerRadius(radii.Window, radii.Window, 0, 0));
            Set(app, "ShellAssistantBubbleRadius", new CornerRadius(
                Math.Min(radii.Card, radii.Control / 2d), radii.Card, radii.Card, radii.Card));
            Set(app, "ShellUserBubbleRadius", new CornerRadius(
                radii.Card, radii.Card, Math.Min(radii.Card, radii.Control / 2d), radii.Card));
            Set(app, "ControlCornerRadius", new CornerRadius(radii.Control));
            Set(app, "OverlayCornerRadius", new CornerRadius(radii.Card));
            Set(app, "FlyoutPresenterCornerRadius", new CornerRadius(radii.Card));
            Set(app, "MenuFlyoutPresenterCornerRadius", new CornerRadius(radii.Card));
            Set(app, "ShellFontFamily", fontCatalog.Resolve(settings.AppFontFamily, ShellTheme.Marelian));
        }
        if (Avalonia.Application.Current is { } current) ThemeVisualTokens.Apply(current, profile);
        AnimatedIcon.RefreshAll();
        PaletteApplied?.Invoke(palette);
        return palette;
    }

    public ThemeColorDefaults GetDefaults(ShellTheme theme, ShellThemeMode mode)
    {
        var palette = Marelian(mode);
        return new(ToHex(palette.Accent), ToHex(palette.Background));
    }

    /// <summary>
    /// The category colours for one palette setting and one theme variant.
    ///
    /// Each mode is asked for in its own terms rather than being the same ladder read louder or
    /// quieter - see <see cref="CategoryPalette"/> for why a palette that differs from the
    /// default only by its chroma is not the thing its name promises. What the modes share is
    /// the order they run in, so switching between them recolours the categories without
    /// reshuffling which of them is the warm one.
    ///
    /// Light variants sit lower on the lightness scale than dark ones throughout: a category
    /// colour has to hold its own against the surface behind it in both directions.
    /// </summary>
    private static VisualAccentPalette BuildVisualPalette(VisualPalette mode, ShellPalette shell)
    {
        var danger = shell.Danger;
        var success = shell.Success;
        var warning = shell.Warning;
        var dark = shell.IsDark;

        var categories = mode switch
        {
            // No hue to tell them apart at all, so the whole separation is weight: nine even
            // steps in the shell's own hue, which keeps the greys part of the theme rather than
            // neutral by accident.
            VisualPalette.Monochrome => CategoryPalette.ByWeight(
                dark ? 0.42 : 0.28, dark ? 0.96 : 0.82, 0.014, HueOf(shell.Accent)),

            // One family around the accent, spread over a full scale of tints and shades. The
            // fan is narrow on purpose: at nine colours the separation comes from weight, and a
            // wider fan would buy almost none of it while spending the whole idea of the mode.
            VisualPalette.Accent => CategoryPalette.Around(
                HueOf(shell.Accent), dark ? 0.46 : 0.30, dark ? 0.95 : 0.80,
                0.060, dark ? 0.190 : 0.200, 14),

            // Chalky and light. Held higher than the default and at two thirds of its chroma,
            // which is the difference between a pastel and a pale version of something else.
            VisualPalette.Pastel => CategoryPalette.Read(
                dark ? 0.830 : 0.690, 0.105, dark ? 0.065 : 0.062),

            // Natural material: its own hues, its own chromas, and a ramp of weight running
            // from a dark brick to a pale clay.
            VisualPalette.Earth => CategoryPalette.Earth(
                dark ? 0.45 : 0.32, dark ? 0.91 : 0.80, dark ? 0.30 : 0.28),

            // Every hue at its own limit rather than all of them at one lightness, which is what
            // separates an electric palette from a merely saturated one.
            VisualPalette.Neon => CategoryPalette.Neon(
                dark ? 0.62 : 0.42, dark ? 0.86 : 0.68, 0.030),

            // The published safe set. On a light shell exactly as designed; on a dark one with
            // its lightness lifted and its hues untouched.
            VisualPalette.ColorBlind => dark
                ? CategoryPalette.SafeSet(0.30, 0.66)
                : CategoryPalette.SafeSet(0.00, 1.00),

            _ => CategoryPalette.Read(dark ? 0.780 : 0.555, dark ? 0.150 : 0.145, 0.050)
        };

        var focus = mode switch
        {
            VisualPalette.Monochrome => Mix(shell.Text, shell.Background, 0.30),
            VisualPalette.Accent => Mix(shell.Accent, dark ? Colors.White : Colors.Black, 0.27),
            _ => Color.Parse(dark ? "#FFB45E" : "#B4761F")
        };

        return new(
            [.. categories.Select(colour => Color.FromRgb(colour.R, colour.G, colour.B))],
            focus, success, warning, danger, shell.Muted);
    }

    /// <summary>Where a colour sits on the OKLCH circle, in degrees.</summary>
    private static double HueOf(Color color)
    {
        var (_, a, b) = CategoryPalette.ToOklab(new PaletteColor(color.R, color.G, color.B));
        var degrees = Math.Atan2(b, a) * 180d / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    public event Action<ShellPalette>? PaletteApplied;

    public ShellPalette ResolvePalette(AppSettings settings, MarelianThemeProfile? customization = null)
    {
        var profile = (customization ?? MarelianAppearance.Profile(settings)).Normalize();
        var preset = Marelian(settings.ShellThemeMode);
        var background = Parse(profile.Background,
            settings.ThemeColorSource == ThemeColorSource.Custom ? Parse(settings.CustomBackground, preset.Background) : preset.Background);
        var palette = background != preset.Background ? WithBackground(preset, background) : preset;
        var accent = settings.ThemeColorSource switch
        {
            ThemeColorSource.Custom => Parse(profile.Accent, Parse(settings.CustomAccent, preset.Accent)),
            ThemeColorSource.SystemAccent => GetSystemAccent() ?? preset.Accent,
            ThemeColorSource.Wallpaper => GetWallpaperAccent() ?? GetSystemAccent() ?? preset.Accent,
            _ => preset.Accent
        };
        if (settings.ThemeColorSource is ThemeColorSource.SystemAccent or ThemeColorSource.Wallpaper)
            accent = NormalizeAccent(accent, palette.IsDark);
        return palette with
        {
            Accent = accent, Surface = Parse(profile.Surface, palette.Surface), SurfaceAlt = Parse(profile.SurfaceAlt, palette.SurfaceAlt),
            Text = Parse(profile.Text, palette.Text), Muted = Parse(profile.Muted, palette.Muted), Stroke = Parse(profile.Stroke, palette.Stroke),
            Secondary = Parse(profile.Secondary, palette.Secondary), Tertiary = Parse(profile.Tertiary, palette.Tertiary),
            Danger = Parse(profile.Danger, palette.IsDark ? Color.Parse("#FF8295") : Color.Parse("#C9435C")),
            Success = Parse(profile.Success, palette.IsDark ? Color.Parse("#55D6A1") : Color.Parse("#168A64")),
            Warning = Parse(profile.Warning, palette.IsDark ? Color.Parse("#F4B860") : Color.Parse("#B76D16")),
            Selection = Parse(profile.Selection, WithAlpha(Mix(accent, background, palette.IsDark ? 0.70 : 0.80), 220))
        };
    }

    public static ShellPalette Marelian(ShellThemeMode mode) => mode == ShellThemeMode.Dark
        ? new(true, Color.Parse("#13110F"), Color.Parse("#1A1818"), Color.Parse("#242020"),
            Color.Parse("#F7F1EA"), Color.Parse("#AEA29B"), Color.Parse("#D75053"), Color.Parse("#3A3030"),
            Color.Parse("#D9967F"), Color.Parse("#93A88E"))
        : new(false, Color.Parse("#F2E8DA"), Color.Parse("#FBF4E9"), Color.Parse("#E8DDCE"),
            Color.Parse("#261D1B"), Color.Parse("#756862"), Color.Parse("#D75053"), Color.Parse("#D8C8B7"),
            Color.Parse("#A34649"), Color.Parse("#73876D"));

    private static ShellPalette WithBackground(ShellPalette preset, Color background)
    {
        var dark = RelativeLuminance(background) < 0.36;
        return dark
            ? preset with
            {
                IsDark = true,
                Background = background,
                Surface = Mix(background, Colors.White, 0.07),
                SurfaceAlt = Mix(background, Colors.White, 0.13),
                Text = Mix(Color.Parse("#FFFDF8"), preset.Accent, 0.04),
                Muted = Mix(Color.Parse("#FFFDF8"), background, 0.50),
                Stroke = Mix(background, Colors.White, 0.22)
            }
            : preset with
            {
                IsDark = false,
                Background = background,
                Surface = Mix(background, Colors.White, 0.56),
                SurfaceAlt = Mix(background, Colors.Black, 0.05),
                Text = Mix(Color.Parse("#17120F"), preset.Accent, 0.04),
                Muted = Mix(Color.Parse("#17120F"), background, 0.48),
                Stroke = Mix(background, Colors.Black, 0.13)
            };
    }

    public void Dispose() { _disposed = true; _linuxAppearance?.Dispose(); }

    private Color? GetSystemAccent()
    {
        if (_linuxAppearance is not null) return _linuxAppearance.Current.Accent;
        if (OperatingSystem.IsMacOS()) return MacOSSystemAppearance.TryGetAccentColor();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("ColorizationColor") is int value)
                {
                    var raw = unchecked((uint)value);
                    return Color.FromRgb((byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);
                }
            }
            catch { }
        }
        return null;
    }

    private Color? GetWallpaperAccent()
    {
        if (_linuxAppearance is not null) return _linuxAppearance.Current.Wallpaper;
        if (OperatingSystem.IsMacOS() && MacOSSystemAppearance.TryGetWallpaperAccent() is { } macAccent)
            return macAccent;
        var path = GetWallpaperPath();
        return SampleWallpaper(path);
    }

    internal static Color? SampleWallpaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap is null) return null;
            var step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 80);
            double red = 0, green = 0, blue = 0, weight = 0;
            for (var y = 0; y < bitmap.Height; y += step)
            for (var x = 0; x < bitmap.Width; x += step)
            {
                var pixel = bitmap.GetPixel(x, y);
                var max = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                var min = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                var saturation = (max - min) / 255d;
                var brightness = (max + min) / 510d;
                var sampleWeight = 0.25 + saturation * 1.8 + (brightness is > 0.18 and < 0.86 ? 0.35 : 0);
                red += pixel.Red * sampleWeight;
                green += pixel.Green * sampleWeight;
                blue += pixel.Blue * sampleWeight;
                weight += sampleWeight;
            }
            return weight <= 0 ? null : Color.FromRgb((byte)(red / weight), (byte)(green / weight), (byte)(blue / weight));
        }
        catch { return null; }
    }

    private static string? GetWallpaperPath()
    {
        if (OperatingSystem.IsMacOS()) return MacOSSystemAppearance.TryGetWallpaperPath();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                return key?.GetValue("WallPaper") as string;
            }
            catch { return null; }
        }
        return null;
    }

    private static Color NormalizeAccent(Color color, bool darkBackground)
    {
        RgbToHsl(color, out var h, out var s, out var l);
        s = Math.Clamp(s, 0.48, 0.86);
        l = darkBackground ? Math.Clamp(l, 0.58, 0.72) : Math.Clamp(l, 0.38, 0.58);
        return HslToRgb(h, s, l);
    }

    private static Color Mix(Color first, Color second, double secondAmount)
    {
        var firstAmount = 1 - secondAmount;
        return Color.FromRgb((byte)(first.R * firstAmount + second.R * secondAmount),
            (byte)(first.G * firstAmount + second.G * secondAmount),
            (byte)(first.B * firstAmount + second.B * secondAmount));
    }

    private static Color BestContrastingTint(Color background, Color darkAnchor, Color lightAnchor)
    {
        var darkTint = Mix(darkAnchor, background, 0.08);
        var lightTint = Mix(lightAnchor, background, 0.08);
        return ContrastRatio(darkTint, background) >= ContrastRatio(lightTint, background)
            ? darkTint
            : lightTint;
    }

    private static Color EnsureContrast(Color candidate, Color background, double minimum, Color anchor)
    {
        if (ContrastRatio(candidate, background) >= minimum) return candidate;
        for (var step = 1; step <= 10; step++)
        {
            var adjusted = Mix(candidate, anchor, step / 10d);
            if (ContrastRatio(adjusted, background) >= minimum) return adjusted;
        }
        return anchor;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Parse(string? value, Color fallback)
        => value is not null && Color.TryParse(value, out var color) ? color : fallback;

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static void Set(Avalonia.Application app, string key, object value) => app.Resources[key] = value;

    private static void RgbToHsl(Color color, out double hue, out double saturation, out double lightness)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        lightness = (max + min) / 2;
        if (Math.Abs(max - min) < 0.0001) { hue = saturation = 0; return; }
        var delta = max - min;
        saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        hue = max == r ? (g - b) / delta + (g < b ? 6 : 0) : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        hue /= 6;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1d / 6) return p + (q - p) * 6 * t;
            if (t < 1d / 2) return q;
            if (t < 2d / 3) return p + (q - p) * (2d / 3 - t) * 6;
            return p;
        }
        if (s == 0) return Color.FromRgb((byte)(l * 255), (byte)(l * 255), (byte)(l * 255));
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q;
        return Color.FromRgb((byte)(Hue(p, q, h + 1d / 3) * 255), (byte)(Hue(p, q, h) * 255), (byte)(Hue(p, q, h - 1d / 3) * 255));
    }

    private static Color ShiftHue(Color color, double degrees)
    {
        RgbToHsl(color, out var h, out var s, out var l);
        var shiftedHue = (h + degrees / 360d) % 1.0;
        if (shiftedHue < 0) shiftedHue += 1.0;
        return HslToRgb(shiftedHue, s, l);
    }
}

public sealed record ShellPalette(
    bool IsDark,
    Color Background,
    Color Surface,
    Color SurfaceAlt,
    Color Text,
    Color Muted,
    Color Accent,
    Color Stroke,
    Color Secondary,
    Color Tertiary)
{
    public Color Danger { get; init; }
    public Color Success { get; init; }
    public Color Warning { get; init; }
    public Color Selection { get; init; }

    public Color Color(ThemeColorRole role) => role switch
    {
        ThemeColorRole.Accent => Accent, ThemeColorRole.Background => Background,
        ThemeColorRole.Surface => Surface, ThemeColorRole.SurfaceAlt => SurfaceAlt,
        ThemeColorRole.Text => Text, ThemeColorRole.Muted => Muted, ThemeColorRole.Stroke => Stroke,
        ThemeColorRole.Secondary => Secondary, ThemeColorRole.Tertiary => Tertiary,
        ThemeColorRole.Danger => Danger, ThemeColorRole.Success => Success,
        ThemeColorRole.Warning => Warning, ThemeColorRole.Selection => Selection, _ => Colors.Black
    };
}
public sealed record ThemeColorDefaults(string Accent, string Background);
