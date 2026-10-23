using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

public sealed class SqliteStoreTests
{
    [Fact]
    public async Task MarelianMigration_UpgradesLegacyCustomTheme()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-marelian-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            await using (var legacy = factory.CreateDbContext())
            {
                await legacy.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET ShellTheme = 'Custom';", cancellationToken);
                await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE AppSettings DROP COLUMN ShellThemeMode;", cancellationToken);
                await legacy.Database.ExecuteSqlRawAsync(
                    "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '202608240005_ThemeModesAndMarelian';",
                    cancellationToken);
            }

            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            await using var upgraded = factory.CreateDbContext();
            var settings = Assert.Single(await upgraded.AppSettings.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(ShellTheme.Marelian, settings.ShellTheme);
            Assert.Equal(ShellThemeMode.Dark, settings.ShellThemeMode);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task EventScheduleRepair_RestoresMissingCalendarEventsTable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-event-repair-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);

            await using (var damagedDb = factory.CreateDbContext())
            {
                await damagedDb.Database.ExecuteSqlRawAsync("DROP TABLE CalendarEvents;", cancellationToken);
                await damagedDb.Database.ExecuteSqlRawAsync(
                    "DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('202608230001_EventScheduleRepair', '202608230002_EventPlanningMetadata');",
                    cancellationToken);
            }

            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);

            ICalendarRepository calendar = new SqliteStore(factory);
            var calendarEvent = new CalendarEvent
            {
                Title = "Restored schedule",
                StartUtc = DateTimeOffset.UtcNow,
                EndUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            await calendar.AddAsync(calendarEvent, cancellationToken);

            var restored = Assert.Single(await calendar.GetAllAsync(cancellationToken));
            Assert.Equal(calendarEvent.Id, restored.Id);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
    [Fact]
    public async Task MigrationAndTaskRoundTrip_WorkOnFreshDatabase()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"intelligent-scheduler-test-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var store = new SqliteStore(factory);
            ITaskRepository tasks = store;

            var task = new TaskItem { Title = "Write integration test", EstimatedDurationMin = 45, RemainingDurationMin = 45, Priority = 80 };
            await tasks.AddAsync(task, cancellationToken);

            var restored = Assert.Single(await tasks.GetAllAsync(cancellationToken));
            Assert.Equal(task.Id, restored.Id);
            Assert.Equal(45, restored.RemainingDurationMin);

            await using var db = factory.CreateDbContext();
            Assert.Empty(await db.AvailabilityRules.ToListAsync(cancellationToken));
            Assert.Empty(await db.PlanningProfiles.ToListAsync(cancellationToken));
            var settings = Assert.Single(await db.AppSettings.ToListAsync(cancellationToken));
            Assert.Equal(AssistantProvider.Local, settings.AssistantProvider);
            Assert.Equal(ShellTheme.Marelian, settings.ShellTheme);
            Assert.Equal(ShellThemeMode.Dark, settings.ShellThemeMode);
            Assert.Equal(VisualPalette.Vibrant, settings.VisualPalette);
            Assert.Equal("builtin:Inter", settings.AppFontFamily);
            Assert.True(settings.EnableTransparency);
            // A fresh profile starts with a trace of the desktop showing through rather than a
            // page of text laid over it; the slider is what asks for more.
            Assert.Equal(10, settings.TransparencyIntensity);
            Assert.Equal(string.Empty, settings.LanguageCode);
            Assert.True(settings.KeepOnTop);
            Assert.True(settings.Use24HourClock);
            // The quick widget still times out unless it is told not to.
            Assert.False(settings.WidgetKeepOpen);

            IAvailabilityRepository availability = store;
            await availability.AddExceptionAsync(new AvailabilityException
            {
                Date = new DateOnly(2026, 8, 20), StartLocal = new TimeOnly(12, 0), EndLocal = new TimeOnly(16, 0),
                Kind = AvailabilityExceptionKind.Unavailable
            }, cancellationToken);
            Assert.Single(await availability.GetExceptionsAsync(cancellationToken));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task EdgeShellMigration_UpgradesV01DatabaseWithoutLosingSettings()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"intelligent-scheduler-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            await using (var oldDb = factory.CreateDbContext())
            {
                foreach (var command in new[]
                {
                    "ALTER TABLE AppSettings DROP COLUMN AssistantProvider;", "ALTER TABLE AppSettings DROP COLUMN AssistantEndpoint;",
                    "ALTER TABLE AppSettings DROP COLUMN AssistantModel;", "ALTER TABLE AppSettings DROP COLUMN AssistantApiKeyCiphertext;",
                    "ALTER TABLE AppSettings DROP COLUMN McpTransport;", "ALTER TABLE AppSettings DROP COLUMN McpCommand;",
                    "ALTER TABLE AppSettings DROP COLUMN McpArguments;", "ALTER TABLE AppSettings DROP COLUMN McpToolName;",
                    "ALTER TABLE AppSettings DROP COLUMN ShellTheme;", "ALTER TABLE AppSettings DROP COLUMN ThemeColorSource;",
                    "ALTER TABLE AppSettings DROP COLUMN CustomAccent;", "ALTER TABLE AppSettings DROP COLUMN CustomBackground;",
                    "ALTER TABLE AppSettings DROP COLUMN EnableTransparency;", "ALTER TABLE AppSettings DROP COLUMN RevealMode;",
                    "ALTER TABLE AppSettings DROP COLUMN PinOpen;", "ALTER TABLE AppSettings DROP COLUMN AutoPlan;",
                    "ALTER TABLE AppSettings DROP COLUMN VisualPalette;",
                    "ALTER TABLE AppSettings DROP COLUMN AppFontFamily;",
                    "ALTER TABLE AppSettings DROP COLUMN ShellThemeMode;"
                }) await oldDb.Database.ExecuteSqlRawAsync(command, cancellationToken);
                await oldDb.Database.ExecuteSqlRawAsync(
                    "DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('202608170003_EdgeShellRedesign', '202608240003_VisualPalette', '202608240004_AppTypography', '202608240005_ThemeModesAndMarelian');", cancellationToken);
                await oldDb.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET NotificationsEnabled = 0;", cancellationToken);
            }

            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);

            await using var upgraded = factory.CreateDbContext();
            var settings = Assert.Single(await upgraded.AppSettings.AsNoTracking().ToListAsync(cancellationToken));
            Assert.False(settings.NotificationsEnabled);
            Assert.Equal(AssistantProvider.Local, settings.AssistantProvider);
            Assert.Equal(ShellTheme.Marelian, settings.ShellTheme);
            Assert.Equal(ShellThemeMode.Dark, settings.ShellThemeMode);
            Assert.Equal(VisualPalette.Vibrant, settings.VisualPalette);
            Assert.Equal("builtin:Inter", settings.AppFontFamily);
            Assert.Equal(EdgeRevealMode.Hover, settings.RevealMode);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SettingsRoundTrip_PersistsShellAssistantAndLocalizationState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-settings-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ISettingsRepository repository = new SqliteStore(factory);

            var settings = await repository.GetAppSettingsAsync(cancellationToken);
            settings.LanguageCode = "de";
            settings.ShellTheme = ShellTheme.Celestia;
            settings.ShellThemeMode = ShellThemeMode.Light;
            settings.ThemeColorSource = ThemeColorSource.Custom;
            settings.CustomAccent = "#123456";
            settings.CustomBackground = "#234567";
            settings.ThemeColorOverridesJson = "{\"Celestia:Light\":{\"Accent\":\"#123456\",\"Background\":\"#234567\",\"Surface\":\"#345678\",\"TextScale\":1.15,\"WindowRadius\":22}}";
            settings.VisualPalette = VisualPalette.ColorBlind;
            settings.AppFontFamily = "builtin:Geologica";
            settings.EnableTransparency = false;
            settings.TransparencyIntensity = 65;
            settings.RevealMode = EdgeRevealMode.Click;
            settings.PinOpen = true;
            settings.AutoPlan = false;
            settings.AutostartEnabled = true;
            settings.ReduceMotion = true;
            settings.StartHidden = true;
            settings.KeepOnTop = false;
            settings.Use24HourClock = false;
            settings.LastSection = "assistant";
            settings.AssistantProvider = AssistantProvider.OpenAiCompatible;
            settings.AssistantAccessLevel = AssistantAccessLevel.AutoSafe;
            settings.ShareContextWithExternalAi = true;
            settings.AssistantEndpoint = "https://example.test/v1";
            settings.AssistantModel = "test-model";
            settings.AssistantApiKeyCiphertext = "encrypted";
            settings.LocalModelId = "granite-4-8b";
            settings.LocalModelVisionEnabled = true;
            settings.LocalModelIdleMinutes = 30;
            settings.LocalModelAutoTune = false;
            settings.LocalTemperature = 0.65;
            settings.LocalMaxTokens = 1500;
            settings.LocalModelContextTokens = 16384;
            settings.LocalModelThreads = 8;
            settings.CustomModelPath = @"C:\models\mine.gguf";
            settings.AssistantTemperature = 0.9;
            settings.AssistantMaxTokens = 4096;
            settings.AssistantTopP = 0.85;
            settings.AssistantMaxToolIterations = 12;
            settings.AssistantHistoryTurns = 40;
            settings.AssistantTimeoutMinutes = 12;
            settings.AssistantExtraInstructions = "Answer briefly.";
            await repository.UpdateAppSettingsAsync(settings, cancellationToken);

            var restored = await repository.GetAppSettingsAsync(cancellationToken);
            Assert.Equal("de", restored.LanguageCode);
            Assert.Equal(ShellTheme.Marelian, restored.ShellTheme);
            Assert.Equal(ShellThemeMode.Light, restored.ShellThemeMode);
            Assert.Equal(ThemeColorSource.Custom, restored.ThemeColorSource);
            Assert.Equal("#123456", restored.CustomAccent);
            Assert.Equal("#234567", restored.CustomBackground);
            Assert.Contains("Marelian:Light", restored.ThemeColorOverridesJson);
            var appearance = MarelianAppearance.Profile(restored);
            Assert.Equal("#345678", appearance.Surface);
            Assert.Equal(1.15, appearance.TextScale);
            Assert.Equal(22, appearance.WindowRadius);
            Assert.Equal(VisualPalette.ColorBlind, restored.VisualPalette);
            Assert.Equal("builtin:Geologica", restored.AppFontFamily);
            Assert.False(restored.EnableTransparency);
            Assert.Equal(65, restored.TransparencyIntensity);
            Assert.Equal(EdgeRevealMode.Click, restored.RevealMode);
            Assert.Equal("granite-4-8b", restored.LocalModelId);
            Assert.False(restored.LocalModelAutoTune);
            Assert.Equal(0.65, restored.LocalTemperature, 3);
            Assert.Equal(1500, restored.LocalMaxTokens);
            Assert.Equal(16384, restored.LocalModelContextTokens);
            Assert.Equal(8, restored.LocalModelThreads);
            Assert.Equal(@"C:\models\mine.gguf", restored.CustomModelPath);
            Assert.Equal(0.9, restored.AssistantTemperature, 3);
            Assert.Equal(4096, restored.AssistantMaxTokens);
            Assert.Equal(0.85, restored.AssistantTopP, 3);
            Assert.Equal(12, restored.AssistantMaxToolIterations);
            Assert.Equal(40, restored.AssistantHistoryTurns);
            Assert.Equal(12, restored.AssistantTimeoutMinutes);
            Assert.Equal("Answer briefly.", restored.AssistantExtraInstructions);
            Assert.True(restored.PinOpen);
            Assert.False(restored.AutoPlan);
            Assert.True(restored.AutostartEnabled);
            Assert.True(restored.ReduceMotion);
            Assert.True(restored.StartHidden);
            Assert.False(restored.KeepOnTop);
            Assert.False(restored.Use24HourClock);
            Assert.Equal("assistant", restored.LastSection);
            Assert.Equal(AssistantProvider.OpenAiCompatible, restored.AssistantProvider);
            Assert.Equal(AssistantAccessLevel.AutoSafe, restored.AssistantAccessLevel);
            Assert.True(restored.ShareContextWithExternalAi);
            Assert.Equal("https://example.test/v1", restored.AssistantEndpoint);
            Assert.Equal("test-model", restored.AssistantModel);
            Assert.Equal("encrypted", restored.AssistantApiKeyCiphertext);
            Assert.True(restored.LocalModelVisionEnabled);
            Assert.Equal(30, restored.LocalModelIdleMinutes);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CalendarEventMetadata_RoundTripsThroughMigration()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-event-metadata-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ICalendarRepository calendar = new SqliteStore(factory);
            var seriesId = Guid.NewGuid();
            var item = new CalendarEvent
            {
                Title = "Planning review",
                StartUtc = DateTimeOffset.UtcNow,
                EndUtc = DateTimeOffset.UtcNow.AddMinutes(37),
                Category = EventCategory.Work,
                Priority = EventPriority.Critical,
                Flexibility = EventFlexibility.ProposeMove,
                Recurrence = EventRecurrence.Weekly,
                SeriesId = seriesId,
                OccurrenceIndex = 2
            };

            await calendar.AddAsync(item, cancellationToken);

            var restored = Assert.Single(await calendar.GetAllAsync(cancellationToken));
            Assert.Equal(EventCategory.Work, restored.Category);
            Assert.Equal(EventPriority.Critical, restored.Priority);
            Assert.Equal(EventFlexibility.ProposeMove, restored.Flexibility);
            Assert.Equal(EventRecurrence.Weekly, restored.Recurrence);
            Assert.Equal(seriesId, restored.SeriesId);
            Assert.Equal(2, restored.OccurrenceIndex);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
    [Fact]
    public async Task RecurringSeries_UpdateFollowingAndDeleteSeries_AreScoped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-event-series-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ICalendarRepository calendar = new SqliteStore(factory);
            var seriesId = Guid.NewGuid();
            var start = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
            var series = Enumerable.Range(0, 3).Select(index => new CalendarEvent
            {
                Title = "Review",
                StartUtc = start.AddDays(index),
                EndUtc = start.AddDays(index).AddMinutes(30),
                Category = EventCategory.Work,
                Recurrence = EventRecurrence.Daily,
                SeriesId = seriesId,
                OccurrenceIndex = index
            }).ToList();
            await calendar.AddRangeAsync(series, cancellationToken);
            var original = series[1];
            var updated = new CalendarEvent
            {
                Id = original.Id,
                Title = "Moved review",
                StartUtc = original.StartUtc.AddHours(1),
                EndUtc = original.EndUtc.AddHours(1),
                Category = EventCategory.Study,
                Priority = EventPriority.High,
                Recurrence = original.Recurrence,
                SeriesId = seriesId,
                OccurrenceIndex = original.OccurrenceIndex
            };

            await calendar.UpdateScopeAsync(original, updated, EventSeriesScope.ThisAndFollowing, cancellationToken);

            var restored = await calendar.GetAllAsync(cancellationToken);
            Assert.Equal(start, restored[0].StartUtc);
            Assert.Equal(start.AddDays(1).AddHours(1), restored[1].StartUtc);
            Assert.Equal(start.AddDays(2).AddHours(1), restored[2].StartUtc);
            Assert.Equal("Review", restored[0].Title);
            Assert.Equal("Moved review", restored[2].Title);
            Assert.Equal(EventCategory.Work, restored[0].Category);
            Assert.Equal(EventPriority.Normal, restored[0].Priority);
            Assert.All(restored.Skip(1), item => Assert.Equal(EventCategory.Study, item.Category));
            Assert.All(restored.Skip(1), item => Assert.Equal(EventPriority.High, item.Priority));

            await calendar.DeleteScopeAsync(restored[1], EventSeriesScope.Series, cancellationToken);
            Assert.Empty(await calendar.GetAllAsync(cancellationToken));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
    [Fact]
    public async Task RecurringSeries_ReplaceFollowing_PreservesEarlierOccurrence()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-event-replace-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ICalendarRepository calendar = new SqliteStore(factory);
            var originalSeriesId = Guid.NewGuid();
            var replacementSeriesId = Guid.NewGuid();
            var start = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
            var originalSeries = Enumerable.Range(0, 3).Select(index => new CalendarEvent
            {
                Title = "Daily review",
                StartUtc = start.AddDays(index),
                EndUtc = start.AddDays(index).AddMinutes(30),
                Recurrence = EventRecurrence.Daily,
                SeriesId = originalSeriesId,
                OccurrenceIndex = index
            }).ToList();
            await calendar.AddRangeAsync(originalSeries, cancellationToken);
            var replacements = Enumerable.Range(0, 2).Select(index => new CalendarEvent
            {
                Title = "Weekly review",
                StartUtc = start.AddDays(1 + index * 7),
                EndUtc = start.AddDays(1 + index * 7).AddMinutes(45),
                Recurrence = EventRecurrence.Weekly,
                SeriesId = replacementSeriesId,
                OccurrenceIndex = index
            }).ToList();

            await calendar.ReplaceScopeAsync(originalSeries[1], EventSeriesScope.ThisAndFollowing, replacements, cancellationToken);

            var restored = await calendar.GetAllAsync(cancellationToken);
            Assert.Equal(3, restored.Count);
            Assert.Contains(restored, item => item.Id == originalSeries[0].Id && item.SeriesId == originalSeriesId);
            Assert.Equal(2, restored.Count(item => item.SeriesId == replacementSeriesId));
            Assert.DoesNotContain(restored, item => item.Id == originalSeries[1].Id || item.Id == originalSeries[2].Id);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CalendarEvents_ShiftRange_MovesEveryOverlappingEventAndMarksSeriesException()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-event-shift-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ICalendarRepository calendar = new SqliteStore(factory);
            var day = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
            var seriesId = Guid.NewGuid();
            var first = new CalendarEvent
            {
                Title = "Morning",
                StartUtc = day.AddHours(9),
                EndUtc = day.AddHours(10)
            };
            var recurring = new CalendarEvent
            {
                Title = "Review",
                StartUtc = day.AddHours(11),
                EndUtc = day.AddHours(11.5),
                Recurrence = EventRecurrence.Daily,
                SeriesId = seriesId,
                OccurrenceIndex = 2
            };
            var outside = new CalendarEvent
            {
                Title = "Tomorrow",
                StartUtc = day.AddDays(1).AddHours(9),
                EndUtc = day.AddDays(1).AddHours(10)
            };
            await calendar.AddRangeAsync([first, recurring, outside], cancellationToken);

            var shifted = await calendar.ShiftRangeAsync(day, day.AddDays(1), TimeSpan.FromMinutes(75), cancellationToken);

            Assert.Equal(2, shifted);
            var restored = await calendar.GetAllAsync(cancellationToken);
            Assert.Equal(day.AddHours(10).AddMinutes(15), restored.Single(item => item.Id == first.Id).StartUtc);
            var restoredRecurring = restored.Single(item => item.Id == recurring.Id);
            Assert.Equal(day.AddHours(12).AddMinutes(15), restoredRecurring.StartUtc);
            Assert.True(restoredRecurring.IsRecurrenceException);
            Assert.Equal(day.AddDays(1).AddHours(9), restored.Single(item => item.Id == outside.Id).StartUtc);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CalendarEventDelete_RemovesOnlyRequestedEvent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-event-delete-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ICalendarRepository calendar = new SqliteStore(factory);
            var start = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
            var keep = new CalendarEvent { Title = "Keep", StartUtc = start, EndUtc = start.AddHours(1) };
            var remove = new CalendarEvent { Title = "Remove", StartUtc = start.AddHours(2), EndUtc = start.AddHours(3) };
            await calendar.AddAsync(keep, cancellationToken);
            await calendar.AddAsync(remove, cancellationToken);

            await calendar.DeleteAsync(remove.Id, cancellationToken);

            var remaining = await calendar.GetRangeAsync(start.AddDays(-1), start.AddDays(1), cancellationToken);
            var restored = Assert.Single(remaining);
            Assert.Equal(keep.Id, restored.Id);
            Assert.Equal(start, restored.StartUtc);
            Assert.Equal(start.AddHours(1), restored.EndUtc);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task TaskDelete_WorksWhenLegacySchedulingTablesAreMissing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-task-delete-legacy-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            ITaskRepository tasks = new SqliteStore(factory);
            var task = new TaskItem { Title = "Remove unscheduled task", EstimatedDurationMin = 30, RemainingDurationMin = 30 };
            await tasks.AddAsync(task, cancellationToken);

            await using (var legacyDb = factory.CreateDbContext())
            {
                await legacyDb.Database.ExecuteSqlRawAsync("DROP TABLE TaskScheduleExclusions;", cancellationToken);
                await legacyDb.Database.ExecuteSqlRawAsync("DROP TABLE TimeBlocks;", cancellationToken);
            }

            await tasks.DeleteAsync(task.Id, cancellationToken);

            Assert.Empty(await tasks.GetAllAsync(cancellationToken));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
    [Fact]
    public async Task TaskDelete_RemovesActiveBlocksAndPreservesCompletedHistory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-task-delete-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var store = new SqliteStore(factory);
            ITaskRepository tasks = store;
            IBlockRepository blocks = store;
            var task = new TaskItem { Title = "Prepare release", EstimatedDurationMin = 60, RemainingDurationMin = 60 };
            await tasks.AddAsync(task, cancellationToken);
            var start = new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);
            await blocks.AddAsync(new TimeBlock
            {
                TaskId = task.Id, StartUtc = start, EndUtc = start.AddMinutes(30), State = BlockState.Planned, Origin = BlockOrigin.Solver
            }, cancellationToken);
            await blocks.AddAsync(new TimeBlock
            {
                TaskId = task.Id, StartUtc = start.AddDays(-1), EndUtc = start.AddDays(-1).AddMinutes(30),
                State = BlockState.Completed, Origin = BlockOrigin.Solver
            }, cancellationToken);
            await blocks.AddExclusionAsync(new TaskScheduleExclusion
            {
                TaskId = task.Id, StartUtc = start, EndUtc = start.AddHours(1)
            }, cancellationToken);

            await tasks.DeleteAsync(task.Id, cancellationToken);

            await using var db = factory.CreateDbContext();
            Assert.Empty(await db.Tasks.ToListAsync(cancellationToken));
            Assert.Empty(await db.TaskScheduleExclusions.ToListAsync(cancellationToken));
            var history = Assert.Single(await db.TimeBlocks.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(BlockState.Completed, history.State);
            Assert.Null(history.TaskId);
            Assert.Equal("Prepare release", history.TitleOverride);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task PartialBlockCompletion_AtomicallyUpdatesBlockTaskAndExecution()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"daydrive-partial-completion-{Guid.NewGuid():N}.db");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var factory = new TestFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var store = new SqliteStore(factory);
            ITaskRepository tasks = store;
            IBlockRepository blocks = store;
            var task = new TaskItem { Title = "Draft chapter", EstimatedDurationMin = 60, RemainingDurationMin = 60 };
            await tasks.AddAsync(task, cancellationToken);
            var start = new DateTimeOffset(2026, 8, 18, 13, 0, 0, TimeSpan.Zero);
            var block = new TimeBlock
            {
                TaskId = task.Id, StartUtc = start, EndUtc = start.AddMinutes(45), State = BlockState.Planned, Origin = BlockOrigin.Solver
            };
            await blocks.AddAsync(block, cancellationToken);

            var result = await blocks.CompleteTaskBlockAsync(block.Id, 20, cancellationToken);

            Assert.Equal(45, result.PlannedMinutes);
            Assert.Equal(20, result.ActualMinutes);
            Assert.Equal(40, result.RemainingTaskMinutes);
            await using var db = factory.CreateDbContext();
            var storedBlock = Assert.Single(await db.TimeBlocks.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(BlockState.Completed, storedBlock.State);
            Assert.Equal(20, storedBlock.DurationMinutes);
            var storedTask = Assert.Single(await db.Tasks.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(40, storedTask.RemainingDurationMin);
            Assert.Equal(IntelligentScheduler.Domain.TaskStatus.Ready, storedTask.Status);
            var execution = Assert.Single(await db.TaskExecutions.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(45, execution.PlannedMinutes);
            Assert.Equal(20, execution.ActualMinutes);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
