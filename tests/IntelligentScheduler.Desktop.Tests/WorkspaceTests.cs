using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
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
        var chat = window.GetVisualDescendants().OfType<ChatFlyoutView>().Single(view => view.IsEffectivelyVisible);
        Assert.Same(model.NewChatCommand, chat.FindControl<Button>("NewChatButton")!.Command);
        var chatRepository = services.GetRequiredService<IChatRepository>();
        var savedConversation = Pump(chatRepository.CreateConversationAsync("Keep this conversation"));
        Pump(chatRepository.AppendMessageAsync(new ChatMessage
        {
            ConversationId = savedConversation.Id, Role = ChatRole.User, Content = "A saved question"
        }));
        Pump(model.RefreshConversationsAsync());
        foreach (var drawer in new[] { "wrap-up", "history", "memory" })
        {
            model.ActiveConversationId = savedConversation.Id;
            model.Messages.Add(new AssistantMessageViewModel("An existing turn", true));
            model.Conversations.Single().IsSelected = true;
            model.IsWrapUpOpen = drawer == "wrap-up";
            model.IsChatHistoryOpen = drawer == "history";
            model.IsMemoryPanelOpen = drawer == "memory";
            Settle();
            Assert.False(model.ShowChatMain);
            Pump(model.NewChatCommand.ExecuteAsync(null));
            Settle();
            Assert.False(model.IsWrapUpOpen);
            Assert.False(model.IsChatHistoryOpen);
            Assert.False(model.IsMemoryPanelOpen);
            Assert.True(model.ShowChatMain);
            Assert.True(model.ShowChatIntro);
            Assert.True(chat.FindControl<ScrollViewer>("ChatMessagesScroll")!.IsEffectivelyVisible);
            Assert.Same(chat.FindControl<TextBox>("ComposerTextBox"), window.FocusManager!.GetFocusedElement());
            Assert.Null(model.ActiveConversationId);
            Assert.Empty(model.Messages);
            Assert.False(model.Conversations.Single().IsSelected);
        }
        Assert.Equal("A saved question", Assert.Single(Pump(chatRepository.GetMessagesAsync(savedConversation.Id))).Content);
        var accessPicker = chat.FindControl<Button>("AssistantAccessButton")!;
        accessPicker.Flyout!.ShowAt(accessPicker);
        Settle();
        var accessFlyout = Assert.IsType<Flyout>(accessPicker.Flyout);
        var choices = Assert.IsType<StackPanel>(Assert.IsType<Border>(accessFlyout.Content).Child)
            .Children.OfType<AssistantPermissionsView>().Single();
        Assert.Same(model, choices.DataContext);
        CheckNativeWindow("assistant-permissions");
        var permissionButtons = choices.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(3, permissionButtons.Length);
        foreach (var button in permissionButtons)
        {
            button.Command!.Execute(button.CommandParameter);
            Settle();
            Assert.Equal(button.CommandParameter, model.AssistantAccessLevel);
            Assert.Single(permissionButtons, item => item.Classes.Contains("selected"));
        }
        permissionButtons[0].Command!.Execute(permissionButtons[0].CommandParameter);
        permissionButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle();
        Assert.False(accessFlyout.IsOpen);
        model.ToggleChatExpandedCommand.Execute(null);
        Settle();
        Assert.True(planner.IsEffectivelyVisible);
        Assert.True(model.IsChatSheetOpen);
        var sheetChat = window.GetVisualDescendants().OfType<ChatFlyoutView>().Single(view => view.IsEffectivelyVisible);
        var sheetPicker = sheetChat.FindControl<Button>("AssistantAccessButton")!;
        var toolbar = Assert.IsType<Grid>(sheetPicker.Parent);
        var nextTool = toolbar.Children.OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && Grid.GetColumn(button) > 1)
            .OrderBy(Grid.GetColumn).First();
        Assert.True(sheetPicker.Bounds.Right <= nextTool.Bounds.Left, "Permission picker overlaps another composer tool");
        CheckNativeWindow("assistant-sheet");
        window.ShowPlanner();
        model.ToggleEventComposerCommand.Execute(null);
        Settle();
        var eventTitle = planner.FindControl<TextBox>("EventTitleBox")!;
        AssertCenteredInput(eventTitle);
        eventTitle.Text = "A centred event title";
        Settle();
        AssertCenteredInput(eventTitle);
        CheckNativeWindow("centred-input");
        model.OpenEventStartTimePickerCommand.Execute(null);
        Settle();
        var timePicker = Assert.IsType<TimePickerView>(window.FindControl<ContentControl>("PickerContent")!.Content);
        Assert.False(window.FindControl<Grid>("WorkspaceBody")!.IsEnabled);
        foreach (var (start, direction, expected) in new[]
        {
            (603, 1, 605), (603, -1, 600), (605, 1, 610), (605, -1, 600),
            (655, 1, 660), (660, -1, 655), (1435, 1, 0), (0, -1, 1435)
        })
        {
            model.TimePickerValue = TimeSpan.FromMinutes(start);
            if (direction > 0) model.IncrementTimePickerMinuteCommand.Execute(null);
            else model.DecrementTimePickerMinuteCommand.Execute(null);
            Assert.Equal(TimeSpan.FromMinutes(expected), model.TimePickerValue);
        }
        if (!native)
        {
            var wheel = timePicker.FindControl<Border>("MinuteWheel")!;
            var point = wheel.TranslatePoint(new Point(wheel.Bounds.Width / 2, wheel.Bounds.Height / 2), window)!.Value;
            model.TimePickerValue = TimeSpan.FromMinutes(598);
            window.MouseWheel(point, new Vector(0, 1));
            Assert.Equal(TimeSpan.FromMinutes(600), model.TimePickerValue);
            window.MouseWheel(point, new Vector(0, -1));
            Assert.Equal(TimeSpan.FromMinutes(595), model.TimePickerValue);
        }
        CheckNativeWindow("time-picker");
        model.CloseTimePickerCommand.Execute(null);
        model.OpenTaskDurationTimePickerCommand.Execute(null);
        model.TimePickerValue = TimeSpan.Zero;
        model.DecrementTimePickerMinuteCommand.Execute(null);
        Assert.Equal(TimeSpan.Zero, model.TimePickerValue);
        model.TimePickerValue = TimeSpan.FromDays(7);
        model.IncrementTimePickerMinuteCommand.Execute(null);
        Assert.Equal(TimeSpan.FromDays(7), model.TimePickerValue);
        model.CloseTimePickerCommand.Execute(null);
        model.OpenMoveAmountPickerCommand.Execute(null);
        foreach (var current in new[] { 1435, 1439 })
        {
            model.TimePickerValue = TimeSpan.FromMinutes(current);
            model.IncrementTimePickerMinuteCommand.Execute(null);
            Assert.Equal(TimeSpan.FromMinutes(current), model.TimePickerValue);
        }
        model.DecrementTimePickerMinuteCommand.Execute(null);
        Assert.Equal(TimeSpan.FromMinutes(1435), model.TimePickerValue);
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
        Assert.Equal("0.1.2", AppInfo.Version);
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
        AssertCenteredInput(textInput);
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
        var selectedLanguage = model.SelectedLanguage;
        foreach (var code in new[] { "ru", "en" })
        {
            model.SelectedLanguage = model.LanguageOptions.Single(language => language.Code == code);
            Settle();
            Assert.Equal(exported, model.ExportAppearance());
        }
        model.SelectedLanguage = selectedLanguage;
        Settle();
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
            // The native window position is rounded to a physical pixel at fractional scales.
            Assert.InRange(Math.Abs(EdgeHandleWindow.ScreenGap - visualGap), 0, 1 / 1.25);
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
            var digits = capsule.FindControl<StackPanel>("Readout")!.Children.OfType<TextBlock>().ToArray();
            Assert.Equal(3, digits.Length);
            var digitColour = Assert.IsType<SolidColorBrush>(digits[0].Foreground).Color;
            Assert.All(digits, digit =>
            {
                Assert.Equal(FontWeight.Bold, digit.FontWeight);
                Assert.Equal(digitColour, Assert.IsType<SolidColorBrush>(digit.Foreground).Color);
                Assert.Equal(digits[0].Bounds.X, digit.Bounds.X);
                Assert.Equal(digits[0].Bounds.Width, digit.Bounds.Width);
                Assert.Equal(digits[0].Bounds.Height, digit.Bounds.Height);
            });
            Assert.True(skin.CornerRadius.TopLeft < skin.Bounds.Width / 2);
            Assert.Equal(skin.CornerRadius.TopLeft, skin.CornerRadius.TopRight);
            Assert.Equal(skin.CornerRadius.TopLeft, skin.CornerRadius.BottomRight);
            SaveCapsule("capsule-timer");
            capsule.SetSuppressed(true);
            Settle();
            Assert.Equal(restingPosition, capsule.Position);
            Assert.False(capsule.FindControl<StackPanel>("Readout")!.IsVisible);
            capsule.Close();

            CheckReminders();
            CheckEventCardCorners();
            CheckWidgetPlacement();

            void CheckEventCardCorners()
            {
                var mode = model.ShellThemeMode;
                var radius = model.ThemeCardRadius;
                var row = model.Agenda.First(item => item.Event is not null);
                row.IsTrackingOpen = true;
                var rows = new ItemsControl
                {
                    DataContext = model, ItemsSource = new[] { row },
                    ItemTemplate = planner.FindControl<ItemsControl>("AgendaList")!.ItemTemplate
                };
                var preview = new Window
                {
                    Content = rows, Width = 394, SizeToContent = SizeToContent.Height,
                    Background = Brushes.Transparent, WindowDecorations = WindowDecorations.None
                };
                preview.Classes.Add("reduceMotion");
                preview.Show();
                Settle();
                var card = rows.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("agendaCard"));
                var tray = rows.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("rowTrackTray"));
                card.Classes.Add("current");
                foreach (var theme in new[] { "Dark", "Light" })
                foreach (var corners in new[] { 0, 10, 24 })
                {
                    model.SelectThemeModeCommand.Execute(theme);
                    model.ThemeCardRadius = corners;
                    Settle();
                    preview.UpdateLayout();
                    Assert.Equal(Math.Max(0, card.CornerRadius.BottomLeft - card.BorderThickness.Left), tray.CornerRadius.BottomLeft);
                    Assert.Equal(Math.Max(0, card.CornerRadius.BottomRight - card.BorderThickness.Right), tray.CornerRadius.BottomRight);
                    if (snapshots is null) continue;
                    foreach (var scale in new[] { 1d, 1.25, 1.5 })
                    {
                        using var image = new RenderTargetBitmap(
                            new PixelSize((int)Math.Ceiling(preview.ClientSize.Width * scale), (int)Math.Ceiling(preview.ClientSize.Height * scale)),
                            new Vector(96 * scale, 96 * scale));
                        image.Render(preview);
                        image.Save(Path.Combine(snapshots, $"event-corners-{theme}-{corners}-{scale}.png"));
                    }
                }
                preview.Close();
                row.IsTrackingOpen = false;
                model.SelectThemeModeCommand.Execute(mode);
                model.ThemeCardRadius = radius;
            }

            void CheckWidgetPlacement()
            {
                model.ReduceMotion = true;
                model.IsPeekCompact = false;
                model.PeekNextTitle = string.Empty;
                var peek = new QuickPeekWindow { DataContext = model };
                peek.Show();
                peek.SettleNow();
                var area = new PixelRect(-2000, 120, 2000, 1100);
                var scale = 1d;
                var right = 0;
                peek.PlacementChanged += (_, _) => peek.PlaceOn(area, scale, right);
                foreach (var dpi in new[] { 1d, 1.25, 1.5 })
                {
                    scale = dpi;
                    right = area.Right - (int)Math.Round(22 * scale);
                    foreach (var withNext in new[] { true, false })
                    foreach (var withPrompt in new[] { true, false })
                    {
                        model.PeekNextTitle = withNext ? "The next event" : string.Empty;
                        peek.FindControl<Border>("PeekPromptBlock")!.IsVisible = withPrompt;
                        Settle();
                        peek.UpdateLayout();
                        peek.PlaceOn(area, scale, right);
                        CheckCentre();
                    }
                    model.IsPeekCompact = true;
                    Settle();
                    peek.PlaceOn(area, scale, right);
                    CheckCentre();
                    peek.Unfold();
                    Settle();
                    CheckCentre();
                }
                var card = peek.FindControl<Border>("PeekCard")!;
                peek.ResetState();
                // A collapsed widget fits beside the petal instead of sliding in from the screen edge.
                Assert.True(card.Bounds.Width * card.RenderTransform!.Value.M11 < 12);
                Assert.True(card.Bounds.Height * card.RenderTransform!.Value.M22 < 118);
                peek.Close();

                void CheckCentre()
                {
                    Assert.True(Math.Abs(peek.Position.Y + peek.ClientSize.Height * scale / 2 - (area.Y + area.Height / 2d)) <= 1,
                        "The complete widget must stay centred beside the petal when its blocks change");
                    Assert.Equal(right, peek.Position.X + (int)Math.Ceiling((peek.ClientSize.Width - QuickPeekWindow.CardInset) * scale));
                    Assert.True(peek.Position.Y >= area.Y);
                    Assert.True(peek.Position.Y + peek.ClientSize.Height * scale <= area.Bottom + 1);
                    var origin = peek.FindControl<Border>("PeekCard")!.RenderTransformOrigin;
                    Assert.Equal(RelativeUnit.Absolute, origin.Unit);
                    Assert.Equal(area.Y + area.Height / 2d, peek.Position.Y + origin.Point.Y * scale, 5);
                    Assert.Equal(area.Right - 11 * scale, peek.Position.X + origin.Point.X * scale, 5);
                }
            }

            void CheckReminders()
            {
                var starts = DateTimeOffset.Now.AddMinutes(5);
                model.EventTitle = "Design review and planning for the next milestone";
                model.EventDate = starts;
                model.EventTime = starts.TimeOfDay;
                model.EventEndTime = starts.AddHours(1).TimeOfDay;
                model.SelectedDate = DateOnly.FromDateTime(starts.Date);
                Pump(model.AddEventCommand.ExecuteAsync(null));
                Assert.True(model.HasPeekEvent);
                Assert.True(model.HasPeekTimer);
                model.ReduceMotion = false;
                var peek = new QuickPeekWindow { DataContext = model };
                peek.Show();
                peek.SettleNow();
                var engaged = 0;
                var opened = 0;
                peek.Engaged += (_, _) => engaged++;
                peek.ReminderOpened += (_, _) => opened++;
                model.Notifications.Emptied += (_, _) => model.Notifications.EndPresentation();
                foreach (var compact in new[] { true, false })
                foreach (var sameEvent in new[] { true, false })
                {
                    model.IsPeekCompact = compact;
                    model.Notifications.Post(new AppNotification("Starts in 5 minutes", "Design review · 14:00",
                        DateTimeOffset.Now, sameEvent ? model.PeekEventId : Guid.NewGuid()));
                    Settle();
                    peek.SettleNow();
                    peek.UpdateLayout();
                    Assert.True(model.IsPeekReminderDrawn);
                    Assert.True(peek.FindControl<TextBlock>("PeekReminderHeading")!.IsVisible);
                    Assert.False(model.ShowPeekEventTimer);
                    Assert.False(peek.FindControl<StackPanel>("PeekEventTimer")!.IsVisible);
                    Assert.Equal(!compact, peek.FindControl<Border>("PeekClockBlock")!.IsVisible);
                    var dismiss = peek.FindControl<Button>("DismissReminderButton")!;
                    Assert.True(dismiss.IsEffectivelyVisible);
                    Assert.True(dismiss.Bounds.Width <= 12 && dismiss.Bounds.Height <= 40);
                    Assert.Equal(new CornerRadius(dismiss.Bounds.Width / 2), dismiss.CornerRadius);
                    var card = peek.FindControl<Border>("PeekEventBlock")!;
                    if (sameEvent)
                    {
                        var title = card.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Classes.Contains("peekEventTitle"));
                        Assert.True(title.Bounds.Height > title.FontSize * 2, "A long notification title should wrap across multiple lines");
                    }
                    var centre = dismiss.TranslatePoint(new Point(dismiss.Bounds.Width / 2, dismiss.Bounds.Height / 2), peek)!.Value;
                    var cardLeft = card.TranslatePoint(new Point(0, card.Bounds.Height / 2), peek)!.Value;
                    Assert.Equal(6, cardLeft.X - centre.X - dismiss.Bounds.Width / 2, 5);
                    Assert.True(Math.Abs(cardLeft.Y - centre.Y) <= 0.5, "Dismiss petal is not vertically centred");
                    // The whole petal and the gap to the card must stay in the input area.
                    Assert.True(dismiss.TranslatePoint(default, peek)!.Value.X >= QuickPeekWindow.CardInset);
                    if (snapshots is not null)
                    {
                        using var image = new RenderTargetBitmap(new PixelSize((int)peek.ClientSize.Width, (int)peek.ClientSize.Height));
                        image.Render(peek);
                        image.Save(Path.Combine(snapshots, $"notification-{compact}-{sameEvent}.png"));
                    }
                    peek.MouseDown(centre, MouseButton.Left);
                    peek.MouseUp(centre, MouseButton.Left);
                    Assert.False(model.Notifications.HasAny);
                    Assert.Equal(0, engaged);
                    Assert.Equal(0, opened);
                    if (model.IsPeekReminderDrawn) Assert.False(model.ShowPeekEventTimer);
                    Pump(Task.Delay(300));
                    Settle();
                    Assert.False(dismiss.IsVisible);
                    Assert.Equal(!compact, model.ShowPeekEventTimer);
                    var timer = peek.FindControl<StackPanel>("PeekEventTimer")!;
                    Assert.Equal(!compact, timer.IsVisible);
                    Assert.Contains(card, timer.GetVisualAncestors());
                    Assert.Null(peek.FindControl<Border>("PeekTimerBlock"));
                    Assert.Equal(ClockText.Range(starts, starts.AddHours(1), model.Use24HourClock,
                        System.Globalization.CultureInfo.CurrentCulture), peek.FindControl<TextBlock>("PeekEventSchedule")!.Text);
                    Assert.False(peek.FindControl<TextBlock>("PeekReminderHeading")!.IsVisible);
                    Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == model.PeekTimerCaption);
                    if (!compact)
                    {
                        var progress = peek.FindControl<ProgressBar>("PeekEventProgress")!;
                        var digits = timer.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Classes.Contains("peekTimerValue"));
                        Assert.True(progress.Bounds.Height <= 2);
                        Assert.True(progress.Bounds.Top >= digits.Bounds.Bottom);
                    }
                    if (!compact && snapshots is not null)
                    {
                        using var image = new RenderTargetBitmap(new PixelSize((int)peek.ClientSize.Width, (int)peek.ClientSize.Height));
                        image.Render(peek);
                        image.Save(Path.Combine(snapshots, "widget-inline-timer.png"));
                    }
                }
                peek.Close();
            }

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
        void AssertCenteredInput(TextBox input)
        {
            Assert.Equal(VerticalAlignment.Center, input.VerticalContentAlignment);
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            var centre = presenter.TranslatePoint(new Point(0, presenter.Bounds.Height / 2), input)!.Value.Y;
            var expected = (input.Bounds.Height + input.Padding.Top - input.Padding.Bottom) / 2;
            Assert.True(Math.Abs(centre - expected) <= 1, $"Input text is off-centre: {centre} vs {expected}");
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
