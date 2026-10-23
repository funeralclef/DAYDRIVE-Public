using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Desktop;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Application;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void PagesPickersEditingAndResizeStayInsideOneWindow()
    {
        var native = Environment.GetEnvironmentVariable("DAYDRIVE_NATIVE_WM_TEST") == "1";
        var snapshots = Environment.GetEnvironmentVariable("DAYDRIVE_WM_TEST_OUTPUT");
        var root = Path.Combine(Path.GetTempPath(), "daydrive workspace " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("INTELLIGENT_SCHEDULER_DATA_DIR");
        Environment.SetEnvironmentVariable("INTELLIGENT_SCHEDULER_DATA_DIR", root);
        var builder = AppBuilder.Configure<App>().WithInterFont();
        if (native) builder.UsePlatformDetect().With(new X11PlatformOptions { OverlayPopups = true });
        else builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        builder.SetupWithoutStarting();
        using var services = App.BuildServices();
        Pump(services.GetRequiredService<Infrastructure.DatabaseInitializer>().InitializeAsync());
        var model = services.GetRequiredService<EdgeShellViewModel>();
        Pump(model.InitializeAsync());
        var window = new WorkspaceWindow(model);
        window.Show();
        Settle();
        Assert.True(window.CanResize);
        Assert.True(window.ShowInTaskbar);
        Assert.False(window.Topmost);
        var planner = window.GetVisualDescendants().OfType<PlannerView>().Single();
        Assert.Same(window, TopLevel.GetTopLevel(planner));
        CheckNativeWindow("planner");

        model.ShowSettingsCommand.Execute(null);
        Settle();
        var settings = Assert.IsType<SettingsView>(window.FindControl<ContentControl>("SectionContent")!.Content);
        Assert.Same(window, TopLevel.GetTopLevel(settings));
        var identity = NativeIdentity();
        CheckNativeWindow("settings");
        var combo = settings.GetVisualDescendants().OfType<ComboBox>().First(control => control.IsEffectivelyVisible && control.IsEnabled);
        combo.IsDropDownOpen = true;
        Settle();
        Assert.True(combo.IsDropDownOpen);
        CheckNativeWindow("settings-dropdown");
        combo.IsDropDownOpen = false;
        model.ToggleCalendarCommand.Execute(null);
        Settle();
        var calendar = Assert.IsType<CalendarView>(window.FindControl<ContentControl>("SectionContent")!.Content);
        Assert.Same(window, TopLevel.GetTopLevel(calendar));
        CheckNativeWindow("calendar");
        model.ToggleAnalyticsCommand.Execute(null);
        Settle();
        Assert.IsType<AnalyticsView>(window.FindControl<ContentControl>("SectionContent")!.Content);
        CheckNativeWindow("analytics");
        model.ToggleChatCommand.Execute(null);
        model.IsChatExpanded = true;
        Settle();
        Assert.IsType<AssistantView>(window.FindControl<ContentControl>("SectionContent")!.Content);
        CheckNativeWindow("assistant");
        model.ToggleChatExpandedCommand.Execute(null);
        Settle();
        Assert.True(planner.IsEffectivelyVisible);
        Assert.True(model.IsChatSheetOpen);
        window.ShowPlanner();
        model.ToggleEventComposerCommand.Execute(null);
        Settle();
        model.OpenEventStartTimePickerCommand.Execute(null);
        Settle();
        Assert.IsType<TimePickerView>(window.FindControl<ContentControl>("PickerContent")!.Content);
        Assert.False(window.FindControl<Grid>("WorkspaceBody")!.IsEnabled);
        CheckNativeWindow("time-picker");
        model.CloseTimePickerCommand.Execute(null);
        model.IsEventDatePickerOpen = true;
        Settle();
        Assert.IsType<DatePickerView>(window.FindControl<ContentControl>("PickerContent")!.Content);
        CheckNativeWindow("date-picker");
        model.CloseDatePickerCommand.Execute(null);
        model.ToggleEventComposerCommand.Execute(null);
        model.ToggleTaskComposerCommand.Execute(null);
        model.OpenTaskDraftStepsCommand.Execute(null);
        Settle();
        var box = planner.FindControl<TextBox>("TaskDraftStepBox")!;
        box.Text = "A step entered from the keyboard";
        box.Focus();
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Settle();
        Assert.Contains(model.TaskDraftSteps, step => step.Title == "A step entered from the keyboard");
        model.CancelTaskEditCommand.Execute(null);
        model.ShowSettingsCommand.Execute(null);
        Settle();
        Assert.Same(settings, window.FindControl<ContentControl>("SectionContent")!.Content);
        Assert.Equal("0.1.1", AppInfo.Version);
        Assert.Equal(nameof(ShellTheme.Marelian), Assert.Single(model.ThemeOptions).Value);
        model.SelectSettingsSectionCommand.Execute("appearance");
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Background).Value = "#142028";
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Surface).Value = "#203848";
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Text).Value = "#FEDCBA";
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Success).Value = "#55CC88";
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Accent).Value = "#E0A060";
        model.ThemeWindowRadius = 25; model.ThemeControlRadius = 0;
        model.ThemeTextScale = 110; model.ThemeShadowStrength = 0;
        Settle();
        var app = Avalonia.Application.Current!;
        Assert.Equal(Color.Parse("#142028"), app.Resources["ShellTintBackground"]);
        Assert.Equal(Color.Parse("#203848"), app.Resources["ShellTint"]);
        Assert.Equal(Color.Parse("#FEDCBA"), Assert.IsType<SolidColorBrush>(app.Resources["ShellText"]).Color);
        Assert.Equal(Color.Parse("#55CC88"), Assert.IsType<SolidColorBrush>(app.Resources["ShellSuccess"]).Color);
        Assert.Equal(new CornerRadius(25), app.Resources["ShellWindowRadius"]);
        Assert.Equal(new CornerRadius(0), app.Resources["ShellPetalRadius"]);
        Assert.Equal(13.2, Assert.IsType<double>(app.Resources["MarelianFont12"]), 8);
        Assert.Equal(0, Assert.IsType<BoxShadows>(app.Resources["MarelianShadow7"])[0].Color.A);
        CheckNativeWindow("appearance-custom");
        var details = settings.FindControl<Expander>("ThemeDetailsExpander")!;
        details.IsExpanded = true;
        Settle();
        Assert.Contains(settings.GetVisualDescendants().OfType<TextBox>(), box => box.DataContext is ThemeColorEditorViewModel editor
            && editor.Role == ThemeColorRole.Text && box.IsEffectivelyVisible);
        var textInput = settings.GetVisualDescendants().OfType<TextBox>().Single(box => box.DataContext is ThemeColorEditorViewModel editor
            && editor.Role == ThemeColorRole.Text);
        textInput.Text = "#FEDCBB";
        Settle();
        Assert.Equal(Color.Parse("#FEDCBB"), Assert.IsType<SolidColorBrush>(app.Resources["ShellText"]).Color);
        textInput.Text = "#FEDCBA";
        Settle();
        CheckNativeWindow("appearance-colours");
        details.IsExpanded = false;
        var geometry = settings.FindControl<Expander>("ThemeGeometryExpander")!;
        geometry.IsExpanded = true;
        Settle();
        var textSlider = settings.GetVisualDescendants().OfType<Slider>().Single(slider => slider.Minimum == 80 && slider.Maximum == 125);
        Assert.True(textSlider.IsEffectivelyVisible);
        CheckNativeWindow("appearance-geometry");
        geometry.IsExpanded = false;
        var textEditor = model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Text);
        textEditor.Value = "#F";
        model.ThemeCardRadius = 12;
        Settle();
        Assert.True(textEditor.IsInvalid);
        Assert.Equal("#F", textEditor.Value);
        Assert.Equal(Color.Parse("#FEDCBA"), Assert.IsType<SolidColorBrush>(app.Resources["ShellText"]).Color);
        model.SelectThemeModeCommand.Execute("Light");
        Settle();
        Assert.Equal(100, model.ThemeTextScale);
        Assert.Equal(15, model.ThemeWindowRadius);
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Background).Value = "#FFF4E0";
        model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Accent).Value = "#334455";
        Settle();
        model.SelectThemeModeCommand.Execute("Dark");
        Settle();
        Assert.Equal(110, model.ThemeTextScale, 8);
        Assert.Equal(25, model.ThemeWindowRadius);
        Assert.False(textEditor.IsInvalid);
        Assert.Equal("#FEDCBA", textEditor.Value);
        var exported = model.ExportAppearance();
        model.ResetAppearanceCommand.Execute(null);
        Settle();
        Assert.Equal(100, model.ThemeTextScale);
        Assert.True(model.ImportAppearance(exported));
        Settle();
        Assert.Equal(exported, model.ExportAppearance());
        Assert.False(model.ImportAppearance("{\"Format\":\"another-application\"}"));
        Assert.Equal(exported, model.ExportAppearance());
        Pump(model.FlushSettingsAsync());
        var storedTask = services.GetRequiredService<ISettingsRepository>().GetAppSettingsAsync();
        var stored = Pump(storedTask);
        Assert.Equal(ShellTheme.Marelian, stored.ShellTheme);
        Assert.Equal("#FEDCBA", MarelianAppearance.Profile(stored).Text);
        Assert.Equal(1.1, MarelianAppearance.Profile(stored).TextScale);
        Assert.Equal("#FFF4E0", MarelianAppearance.Read(stored.ThemeColorOverridesJson)["Marelian:Light"].Background);
        model.ResetThemeColorsCommand.Execute(null);
        Settle();
        Assert.Equal(nameof(ThemeColorSource.Custom), model.ThemeColorSource);
        Assert.Equal(100, model.ThemeTextScale);
        model.SelectThemeModeCommand.Execute("Light");
        Settle();
        Assert.Equal(Color.Parse("#334455"), Assert.IsType<SolidColorBrush>(app.Resources["ShellAccent"]).Color);
        Assert.Equal("#FFF4E0", model.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Background).Value);
        model.ResetAppearanceCommand.Execute(null);
        Settle();
        if (!native)
        {
            window.Width = 720;
            Settle();
            Assert.False(window.FindControl<ContentControl>("PlannerContent")!.IsVisible);
            Assert.True(settings.IsEffectivelyVisible);
            window.ShowPlanner();
            Settle();
            Assert.True(planner.IsEffectivelyVisible);
        }
        else Assert.Equal(identity, NativeIdentity());
        // The original native shell still hosts the very same reusable planner.
        var edge = new MainWindow { DataContext = model };
        Assert.IsType<PlannerView>(Assert.IsType<Border>(edge.Content).Child);
        edge.Close();
        if (!native)
        {
            // A monitor left of the primary display, with fractional scaling. Starting or
            // folding a session must preserve both the screen gap and the capsule's position.
            model.ReduceMotion = true;
            var capsule = new EdgeHandleWindow { DataContext = model };
            capsule.Show();
            var area = new PixelRect(-2000, 120, 2000, 1100);
            capsule.PlaceOn(area, 1.25);
            Settle();
            var restingPosition = capsule.Position;
            var right = capsule.Position.X + (int)Math.Ceiling(capsule.ClientSize.Width * 1.25);
            Assert.Equal(area.Right, right); // Input reaches the screen edge.
            var skin = capsule.FindControl<Border>("Petal")!;
            var visualGap = (area.Right - capsule.Position.X) / 1.25 - skin.Bounds.Right;
            Assert.Equal(6, visualGap, 8);
            Assert.NotNull(capsule.InputHitTest(new Point(capsule.ClientSize.Width - 1, capsule.ClientSize.Height / 2)));
            Assert.Equal(skin.CornerRadius.TopLeft, skin.CornerRadius.TopRight);
            Assert.Equal(new Thickness(1), skin.BorderThickness);
            var restingWidth = skin.Bounds.Width;
            SaveCapsule("capsule-idle");
            model.PetalHours = "00"; model.PetalMinutes = "12"; model.PetalSeconds = "34";
            capsule.IsLive = true;
            Settle();
            capsule.UpdateLayout();
            Assert.Equal(restingPosition, capsule.Position);
            Assert.True(skin.Bounds.Width > restingWidth);
            Assert.True(capsule.FindControl<StackPanel>("Readout")!.IsVisible);
            Assert.Equal(skin.CornerRadius.TopLeft, skin.CornerRadius.TopRight);
            Assert.Equal(skin.CornerRadius.TopLeft, skin.CornerRadius.BottomRight);
            SaveCapsule("capsule-timer");
            capsule.SetSuppressed(true);
            Settle();
            Assert.Equal(restingPosition, capsule.Position);
            Assert.False(capsule.FindControl<StackPanel>("Readout")!.IsVisible);
            capsule.Close();

            void SaveCapsule(string stage)
            {
                if (snapshots is null) return;
                capsule.UpdateLayout();
                using var image = new RenderTargetBitmap(new PixelSize((int)capsule.ClientSize.Width, (int)capsule.ClientSize.Height));
                image.Render(capsule);
                image.Save(Path.Combine(snapshots, stage + ".png"));
            }
        }
        window.Close();
        Pump(model.FlushSettingsAsync());
        Environment.SetEnvironmentVariable("INTELLIGENT_SCHEDULER_DATA_DIR", previous);

        void Settle()
        {
            var timer = Stopwatch.StartNew();
            var budget = native ? 650 : 80;
            while (timer.ElapsedMilliseconds < budget) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            window.UpdateLayout();
        }
        long? NativeIdentity()
        {
            if (!native) return null;
            var start = new ProcessStartInfo("niri") { RedirectStandardOutput = true };
            start.ArgumentList.Add("msg"); start.ArgumentList.Add("-j"); start.ArgumentList.Add("windows");
            using var process = Process.Start(start)!;
            using var json = JsonDocument.Parse(process.StandardOutput.ReadToEnd());
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(1, json.RootElement.GetArrayLength());
            return json.RootElement[0].GetProperty("id").GetInt64();
        }
        void CheckNativeWindow(string stage)
        {
            if (native) Assert.NotNull(NativeIdentity());
            if (snapshots is null) return;
            Directory.CreateDirectory(snapshots);
            using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
            image.Render(window);
            image.Save(Path.Combine(snapshots, stage + ".png"));
        }
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(90))
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted, "UI operation timed out");
        task.GetAwaiter().GetResult();
    }
}
