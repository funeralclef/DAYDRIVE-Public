using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Desktop.Platform;

namespace IntelligentScheduler.Desktop.ViewModels;

public sealed partial class EdgeShellViewModel
{
    private MarelianThemeProfile _appearanceProfile = new();
    private bool _loadingAppearance;
    private bool _appearanceAttached;
    public ObservableCollection<ThemeColorEditorViewModel> ThemeColors { get; } = [];
    public IEnumerable<ThemeColorEditorViewModel> PrimaryThemeColors => ThemeColors.Take(2);
    public IEnumerable<ThemeColorEditorViewModel> DetailThemeColors => ThemeColors.Skip(2);
    public string ImportThemeTitle => localization.Text("ImportTheme");
    public string ExportThemeTitle => localization.Text("ExportTheme");
    [ObservableProperty] private double _themeWindowRadius = 15;
    [ObservableProperty] private double _themeCardRadius = 10;
    [ObservableProperty] private double _themeItemRadius = 7;
    [ObservableProperty] private double _themeControlRadius = 6;
    [ObservableProperty] private double _themeTextScale = 100;
    [ObservableProperty] private double _themeShadowStrength = 100;
    [ObservableProperty] private string _appearanceStatus = "";
    public event EventHandler<bool>? ThemeFileRequested;

    private void LoadAppearanceControls(MarelianThemeProfile profile)
    {
        _loadingAppearance = true;
        try
        {
            _appearanceProfile = profile.Normalize();
            ThemeWindowRadius = profile.WindowRadius; ThemeCardRadius = profile.CardRadius;
            ThemeItemRadius = profile.ItemRadius; ThemeControlRadius = profile.ControlRadius;
            ThemeTextScale = profile.TextScale * 100; ThemeShadowStrength = profile.ShadowStrength * 100;
            if (ThemeColors.Count == 0)
                foreach (var role in Enum.GetValues<ThemeColorRole>())
                    ThemeColors.Add(new(role, ChangeAppearanceColor, editor =>
                    {
                        foreach (var other in ThemeColors.Where(other => other != editor)) other.IsExpanded = false;
                        IsMascotColorPickerOpen = false;
                    }));
            RefreshAppearanceLabels();
            if (!_appearanceAttached)
            {
                themeService.PaletteApplied += RefreshAppearanceDefaults;
                _appearanceAttached = true;
            }
            var palette = AppearancePalette();
            foreach (var editor in ThemeColors) editor.Load(EditorValue(editor), palette.Color(editor.Role));
        }
        finally { _loadingAppearance = false; }
    }

    private ShellPalette AppearancePalette()
    {
        var preview = new AppSettings
        {
            ShellThemeMode = Parse(ShellThemeMode, Domain.ShellThemeMode.Dark),
            ThemeColorSource = Parse(ThemeColorSource, Domain.ThemeColorSource.Preset),
            CustomAccent = CustomAccent, CustomBackground = CustomBackground
        };
        return themeService.ResolvePalette(preview, _appearanceProfile);
    }

    private void RefreshAppearanceDefaults(ShellPalette palette)
    {
        foreach (var editor in ThemeColors) editor.Update(EditorValue(editor), palette.Color(editor.Role));
    }

    private string? EditorValue(ThemeColorEditorViewModel editor) => editor.Role == ThemeColorRole.Accent
        && ThemeColorSource != nameof(Domain.ThemeColorSource.Custom) ? null : _appearanceProfile.Color(editor.Role);

    private void RefreshAppearanceLabels()
    {
        foreach (var editor in ThemeColors) editor.Label = localization.Text("ThemeColor" + editor.Role);
    }

    private void ChangeAppearanceColor(ThemeColorRole role, string? value)
    {
        if (_loadingAppearance || _loadingSettings) return;
        _appearanceProfile = _appearanceProfile.WithColor(role, value);
        _changingThemeColors = true;
        try
        {
            if (role == ThemeColorRole.Accent)
            {
                CustomAccent = value ?? themeService.GetDefaults(Domain.ShellTheme.Marelian, Parse(ShellThemeMode, Domain.ShellThemeMode.Dark)).Accent;
                ThemeColorSource = value is null ? nameof(Domain.ThemeColorSource.Preset) : nameof(Domain.ThemeColorSource.Custom);
                SelectedColorSourceOption = ColorSourceOptions.FirstOrDefault(option => option.Value == ThemeColorSource);
            }
            if (role == ThemeColorRole.Background)
                CustomBackground = value ?? themeService.GetDefaults(Domain.ShellTheme.Marelian, Parse(ShellThemeMode, Domain.ShellThemeMode.Dark)).Background;
        }
        finally { _changingThemeColors = false; }
        ScheduleThemeColorPreview();
    }

    private void ChangeLegacyAppearanceColor(ThemeColorRole role, string value)
    {
        if (MarelianAppearance.Hex(value) is { } hex) _appearanceProfile = _appearanceProfile.WithColor(role, hex);
    }

    partial void OnThemeWindowRadiusChanged(double value) => AppearanceGeometryChanged();
    partial void OnThemeCardRadiusChanged(double value) => AppearanceGeometryChanged();
    partial void OnThemeItemRadiusChanged(double value) => AppearanceGeometryChanged();
    partial void OnThemeControlRadiusChanged(double value) => AppearanceGeometryChanged();
    partial void OnThemeTextScaleChanged(double value)
    {
        OnPropertyChanged(nameof(ChatBodyFontSize));
        OnPropertyChanged(nameof(ChatBodyLineHeight));
        AppearanceGeometryChanged();
    }
    partial void OnThemeShadowStrengthChanged(double value) => AppearanceGeometryChanged();

    private void AppearanceGeometryChanged()
    {
        if (_loadingAppearance || _loadingSettings) return;
        _appearanceProfile = (_appearanceProfile with
        {
            WindowRadius = ThemeWindowRadius, CardRadius = ThemeCardRadius, ItemRadius = ThemeItemRadius,
            ControlRadius = ThemeControlRadius, TextScale = ThemeTextScale / 100, ShadowStrength = ThemeShadowStrength / 100
        }).Normalize();
        ScheduleThemeColorPreview();
    }

    [RelayCommand] private void ImportTheme() => ThemeFileRequested?.Invoke(this, false);
    [RelayCommand] private void ExportTheme() => ThemeFileRequested?.Invoke(this, true);

    public string ExportAppearance()
    {
        SaveActiveThemeColors();
        return new MarelianThemeFile
        {
            Dark = _themeColorOverrides.GetValueOrDefault(MarelianAppearance.Key(Domain.ShellThemeMode.Dark)) ?? new(),
            Light = _themeColorOverrides.GetValueOrDefault(MarelianAppearance.Key(Domain.ShellThemeMode.Light)) ?? new(),
            Mode = Parse(ShellThemeMode, Domain.ShellThemeMode.Dark), AccentSource = Parse(ThemeColorSource, Domain.ThemeColorSource.Preset),
            CategoryPalette = Parse(VisualPalette, Domain.VisualPalette.Vibrant), Font = AppFontFamily,
            Transparency = EnableTransparency, TransparencyIntensity = TransparencyIntensity,
            ReduceMotion = ReduceMotion, ShowMascot = ShowMascot, MascotColor = UseThemeMascotColor ? null : MascotColor
        }.Serialize();
    }

    public bool ImportAppearance(string content)
    {
        MarelianThemeFile file;
        try { file = MarelianThemeFile.Parse(content); }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
        {
            AppearanceStatus = localization.Text("ThemeFileInvalid");
            return false;
        }
        ApplyAppearanceFile(file);
        AppearanceStatus = localization.Text("ThemeImported");
        return true;
    }

    public void ThemeExported() => AppearanceStatus = localization.Text("ThemeExported");
    public void ThemeFileFailed() => AppearanceStatus = localization.Text("ThemeFileInvalid");

    [RelayCommand]
    private void ResetAppearance() => ApplyAppearanceFile(new MarelianThemeFile { Mode = Parse(ShellThemeMode, Domain.ShellThemeMode.Dark) });

    private void ApplyAppearanceFile(MarelianThemeFile file)
    {
        var wasLoading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            _themeColorOverrides.Clear();
            _themeColorOverrides[MarelianAppearance.Key(Domain.ShellThemeMode.Dark)] = file.Dark;
            _themeColorOverrides[MarelianAppearance.Key(Domain.ShellThemeMode.Light)] = file.Light;
            ShellTheme = nameof(Domain.ShellTheme.Marelian);
            ShellThemeMode = file.Mode.ToString();
            ThemeColorSource = file.AccentSource.ToString(); VisualPalette = file.CategoryPalette.ToString();
            AppFontFamily = fontCatalog.GetAvailableFonts().Any(font => font.Value == file.Font) ? file.Font : FontCatalogService.InterId;
            EnableTransparency = file.Transparency; TransparencyIntensity = file.TransparencyIntensity;
            ReduceMotion = file.ReduceMotion; ShowMascot = file.ShowMascot;
            MascotColor = file.MascotColor ?? ""; UseThemeMascotColor = string.IsNullOrEmpty(file.MascotColor);
            _activeThemeColorKey = MarelianAppearance.Key(file.Mode);
            var active = _themeColorOverrides[_activeThemeColorKey];
            var defaults = themeService.GetDefaults(Domain.ShellTheme.Marelian, file.Mode);
            CustomAccent = active.Accent ?? defaults.Accent; CustomBackground = active.Background ?? defaults.Background;
            LoadAppearanceControls(active);
            BuildLocalizedChoices();
        }
        finally { _loadingSettings = wasLoading; }
        PreviewCurrentTheme();
        ScheduleSettingsSave();
    }
}
