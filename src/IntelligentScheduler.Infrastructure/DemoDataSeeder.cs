using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Infrastructure;

public sealed record DemoDataOptions(
    /// <summary>How much history to generate. 120 covers the 90-day range plus its comparison window.</summary>
    int Days = 120,
    /// <summary>Days of upcoming schedule, so the calendar and "next event" card are not empty.</summary>
    int FutureDays = 7,
    /// <summary>Fixed by default so two runs produce identical numbers.</summary>
    int RandomSeed = 20260824);

public sealed record DemoDataSummary(int Events, int FocusBlocks, int Executions, int Tasks, int Outcomes)
{
    public override string ToString()
        => $"{Events} events, {FocusBlocks} focus blocks, {Executions} completed sessions, " +
           $"{Outcomes} recorded outcomes, {Tasks} tasks";
}

/// <summary>
/// Generates a plausible work history so the analytics panel can be exercised without living
/// with the app for three months.
///
/// The shape is deliberate rather than random: sessions run consistently longer than planned,
/// mornings carry most of the finished work, a meaningful share of blocks were moved, and the
/// recent weeks are busier than the ones before them. That way every metric, every chart and
/// most insights have something real to describe instead of collapsing to "not enough data".
///
/// Generated events carry <see cref="CalendarEvent.Source"/> = "Demo" so they can be told apart
/// from real ones later.
/// </summary>
public static class DemoDataSeeder
{
    public const string SourceMarker = "Demo";

    public static async Task<DemoDataSummary> SeedAsync(
        ICalendarRepository events,
        ITaskRepository tasks,
        IBlockRepository blocks,
        ITaskExecutionRepository executions,
        DemoDataOptions? options = null,
        CancellationToken cancellationToken = default,
        IEventOutcomeRepository? outcomes = null,
        IProjectRepository? projects = null)
    {
        options ??= new DemoDataOptions();
        var random = new Random(options.RandomSeed);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var history = Math.Clamp(options.Days, 7, 400);
        var future = Math.Clamp(options.FutureDays, 0, 60);

        var generated = new List<CalendarEvent>();
        var focusBlocks = new List<TimeBlock>();
        var completions = new List<TaskExecution>();

        for (var offset = -history; offset <= future; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = today.AddDays(offset);
            var isPast = offset < 0;
            // The last four weeks are busier than what came before, so the comparison
            // arrows next to each metric have something to point at.
            var busy = offset > -28;
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            if (weekend)
            {
                if (Chance(random, 0.7)) generated.Add(Event(date, 11, 90, "Errands", EventCategory.Personal));
                if (Chance(random, 0.5)) generated.Add(Event(date, 16, 120, "Family time", EventCategory.Personal));
                if (Chance(random, 0.3)) generated.Add(Event(date, 10, 60, "Long run", EventCategory.Health));
            }
            else
            {
                generated.Add(Event(date, 9, 30, "Daily sync", EventCategory.Work));

                if (Chance(random, busy ? 0.85 : 0.6))
                {
                    var length = Chance(random, 0.4) ? 120 : 90;
                    generated.Add(Event(date, 10, length, "Deep work", EventCategory.Work, EventPriority.High));
                }

                if (Chance(random, 0.75)) generated.Add(Event(date, 13, 45, "Lunch", EventCategory.Personal));

                if (Chance(random, busy ? 0.55 : 0.35))
                    generated.Add(Event(date, 15, 60, PickMeeting(random), EventCategory.Work));

                // A deliberate double booking every couple of weeks: overlapping minutes must
                // be counted once, and this is the data that proves it.
                if (Chance(random, 0.08))
                    generated.Add(Event(date, 15, 45, "Conflicting call", EventCategory.General, EventPriority.Critical));

                if (date.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday && Chance(random, 0.85))
                    generated.Add(Event(date, 19, 60, "Training", EventCategory.Health));

                if (date.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday && Chance(random, 0.7))
                    generated.Add(Event(date, 20, 60, "Language class", EventCategory.Study));

                if (Chance(random, 0.2)) generated.Add(Event(date, 18, 45, "Reading", EventCategory.Study));
            }

            if (!isPast) continue;

            // One or two focus sessions a day, weighted towards the morning: that weighting is
            // what makes the "strong time of day" insight true rather than invented.
            var sessions = weekend ? (Chance(random, 0.35) ? 1 : 0) : Chance(random, busy ? 0.85 : 0.65) ? 2 : 1;
            for (var index = 0; index < sessions; index++)
            {
                var morning = index == 0 ? Chance(random, 0.85) : Chance(random, 0.2);
                var hour = morning ? 8 + random.Next(0, 3) : 14 + random.Next(0, 5);
                var planned = new[] { 30, 45, 60, 90 }[random.Next(4)];
                var plannedStart = Local(date, hour);

                // Roughly a third of sessions had to be moved from their original slot.
                var moved = Chance(random, 0.32);
                var originalStart = moved ? plannedStart.AddMinutes(-(30 + random.Next(0, 90))) : (DateTimeOffset?)null;

                var state = Chance(random, 0.14)
                    ? BlockState.Skipped
                    : BlockState.Completed;

                focusBlocks.Add(new TimeBlock
                {
                    TitleOverride = morning ? "Focus block" : "Afternoon focus",
                    StartUtc = plannedStart.ToUniversalTime(),
                    EndUtc = plannedStart.AddMinutes(planned).ToUniversalTime(),
                    State = state,
                    Origin = BlockOrigin.Manual,
                    PreviousStartUtc = originalStart?.ToUniversalTime()
                });

                if (state != BlockState.Completed) continue;

                // Sessions consistently overrun by roughly a quarter, with noise on top.
                var overrun = 1.0 + 0.25 + (random.NextDouble() - 0.5) * 0.3;
                var actual = Math.Max(5, (int)Math.Round(planned * overrun));
                completions.Add(new TaskExecution
                {
                    TaskId = Guid.NewGuid(),
                    TimeBlockId = focusBlocks[^1].Id,
                    PlannedMinutes = planned,
                    ActualMinutes = actual,
                    CompletedAtUtc = plannedStart.AddMinutes(actual).ToUniversalTime()
                });
            }
        }

        await events.AddRangeAsync(generated, cancellationToken);

        // Recorded verdicts on real events: this is what fills completion, plan accuracy and
        // on-time start once a user starts pressing the buttons on the day card.
        var recorded = 0;
        if (outcomes is not null)
        {
            var stored = await events.GetAllAsync(cancellationToken);
            foreach (var item in stored.Where(item => item.EndUtc < DateTimeOffset.UtcNow))
            {
                // Only about two thirds of past events get a verdict; the rest stay unreported,
                // which is what a real week looks like.
                if (!Chance(random, 0.66)) continue;
                var planned = Math.Max(1, (int)Math.Round((item.EndUtc - item.StartUtc).TotalMinutes));
                var roll = random.NextDouble();
                var kind = roll switch
                {
                    < 0.72 => EventOutcomeKind.Completed,
                    < 0.88 => EventOutcomeKind.Partial,
                    _ => EventOutcomeKind.Skipped
                };
                var actual = kind switch
                {
                    EventOutcomeKind.Completed => (int)Math.Round(planned * (1.15 + random.NextDouble() * 0.25)),
                    EventOutcomeKind.Partial => Math.Max(1, (int)Math.Round(planned * (0.3 + random.NextDouble() * 0.3))),
                    _ => 0
                };
                var delay = kind == EventOutcomeKind.Skipped ? 0 : random.Next(0, 26);

                await outcomes.RecordAsync(new EventOutcome
                {
                    EventId = item.Id,
                    Kind = kind,
                    PlannedStartUtc = item.StartUtc,
                    PlannedMinutes = planned,
                    StartedAtUtc = kind == EventOutcomeKind.Skipped ? null : item.StartUtc.AddMinutes(delay),
                    FinishedAtUtc = item.EndUtc,
                    ActualMinutes = actual
                }, cancellationToken);
                recorded++;
            }
        }
        foreach (var block in focusBlocks) await blocks.AddAsync(block, cancellationToken);
        foreach (var execution in completions) await executions.RecordAsync(execution, cancellationToken);

        var demoTasks = BuildTasks(today);
        foreach (var task in demoTasks) await tasks.AddAsync(task, cancellationToken);

        if (projects is not null) await SeedProjectsAsync(projects, events, tasks, today, cancellationToken);

        return new(generated.Count, focusBlocks.Count, completions.Count, demoTasks.Count, recorded);
    }

    /// <summary>
    /// Projects of both kinds. Two unscheduled ones in flight — one half done, with a day that
    /// slipped and a step booked for this afternoon, and one only just planned — which is enough for
    /// the progress bars, a step carried into today and a slot on the calendar that says which project
    /// it is for. And two scheduled ones: this morning's deep work with its checklist half ticked, and
    /// a weekly review whose one list every Friday carries.
    /// </summary>
    private static async Task SeedProjectsAsync(IProjectRepository projects, ICalendarRepository events,
        ITaskRepository tasks, DateOnly today, CancellationToken cancellationToken)
    {
        var websiteTask = Task("Launch the new website", 60, 80, today.AddDays(4), 23, PreferredTimeOfDay.Any);
        websiteTask.DeadlineUtc = websiteTask.DeadlineUtc?.AddMinutes(59);
        await tasks.AddAsync(websiteTask, cancellationToken);
        var website = new Project { Title = websiteTask.Title, Category = EventCategory.Work, TaskId = websiteTask.Id };
        await projects.AddProjectAsync(website, cancellationToken);
        var websiteSteps = await AddStepsAsync(projects, website, today, cancellationToken,
            ("Collect references", 60, -3, true),
            ("Outline the page structure", 90, -3, true),
            ("Wireframe the landing page", 120, -2, true),
            ("Design the landing page", 150, -1, false),
            ("Design the pricing page", 120, 0, false),
            ("Write the hero copy", 45, 0, false),
            ("Build the landing page", 180, 1, false),
            ("Responsive pass", 90, 2, false),
            ("Test on phones", 60, null, false));

        // One step already has its hour, the way it would after "set a time".
        var booked = websiteSteps[4];
        var slot = Event(today, 16, booked.DurationMinutes, booked.Title, EventCategory.Work);
        await events.AddAsync(slot, cancellationToken);
        booked.EventId = slot.Id;
        await projects.UpdateStepAsync(booked, cancellationToken);

        var talkTask = Task("Prepare the conference talk", 60, 55, today.AddDays(9), 23, PreferredTimeOfDay.Any);
        talkTask.DeadlineUtc = talkTask.DeadlineUtc?.AddMinutes(59);
        await tasks.AddAsync(talkTask, cancellationToken);
        var talk = new Project { Title = talkTask.Title, Category = EventCategory.Study, TaskId = talkTask.Id };
        await projects.AddProjectAsync(talk, cancellationToken);
        var talkSteps = await AddStepsAsync(projects, talk, today, cancellationToken,
            ("Pick the topic", 30, -1, true),
            ("Draft the outline", 60, 1, false),
            ("Make the slides", 120, 3, false),
            ("Rehearse twice", 60, 6, false));

        // The tasks carry their projects' figures, the way the shell keeps them.
        if (ProjectTaskMirror.Apply(websiteTask, websiteSteps)) await tasks.UpdateAsync(websiteTask, cancellationToken);
        if (ProjectTaskMirror.Apply(talkTask, talkSteps)) await tasks.UpdateAsync(talkTask, cancellationToken);

        // This morning's deep work, broken into what it is for. Half of it is ticked if the morning
        // has already happened.
        var (dayStart, dayEnd) = LocalDateRange.Utc(today);
        var deepWork = (await events.GetRangeAsync(dayStart, dayEnd, cancellationToken))
            .FirstOrDefault(item => item.Title == "Deep work");
        if (deepWork is null)
        {
            deepWork = Event(today, 10, 120, "Deep work", EventCategory.Work, EventPriority.High);
            await events.AddAsync(deepWork, cancellationToken);
        }

        var sitting = new Project { Title = deepWork.Title, Category = deepWork.Category, EventId = deepWork.Id };
        await projects.AddProjectAsync(sitting, cancellationToken);
        var sittingSteps = Checklist(sitting,
            ("Reproduce the export bug", 20),
            ("Fix rounding in the invoice builder", 45),
            ("Write the regression test", 25),
            ("Update the changelog", 10));
        await projects.AddStepsAsync(sittingSteps, cancellationToken);
        if (deepWork.StartUtc < DateTimeOffset.UtcNow)
        {
            await projects.CheckAsync(deepWork.Id, [sittingSteps[0].Id], deepWork.StartUtc.AddMinutes(25), cancellationToken);
            await projects.CheckAsync(deepWork.Id, [sittingSteps[1].Id], deepWork.StartUtc.AddMinutes(70), cancellationToken);
        }

        // A weekly review on Fridays, from the most recent one on: one list, ticked afresh each week.
        var friday = today.AddDays(-(((int)today.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7));
        var reviewStart = Local(friday, 17);
        var review = new CalendarEvent
        {
            Title = "Weekly review",
            StartUtc = reviewStart.ToUniversalTime(),
            EndUtc = reviewStart.AddMinutes(45).ToUniversalTime(),
            Category = EventCategory.Personal,
            Recurrence = EventRecurrence.Weekly,
            Source = SourceMarker
        };
        var reviews = EventRecurrenceExpander.Expand(review, reviewStart.AddDays(7 * 8).ToUniversalTime());
        await events.AddRangeAsync(reviews, cancellationToken);
        var weekly = new Project { Title = review.Title, Category = review.Category, SeriesId = reviews[0].SeriesId };
        await projects.AddProjectAsync(weekly, cancellationToken);
        await projects.AddStepsAsync(Checklist(weekly,
            ("Empty the inbox", 10),
            ("Look back at the week", 15),
            ("Pick next week's three priorities", 20)), cancellationToken);
    }

    private static List<ProjectStep> Checklist(Project project, params (string Title, int Minutes)[] steps)
        => [.. steps.Select((step, index) => new ProjectStep
        {
            ProjectId = project.Id,
            Title = step.Title,
            DurationMinutes = step.Minutes,
            SortOrder = index
        })];

    private static async Task<List<ProjectStep>> AddStepsAsync(IProjectRepository projects, Project project,
        DateOnly today, CancellationToken cancellationToken, params (string Title, int Minutes, int? Offset, bool Done)[] steps)
    {
        var added = new List<ProjectStep>();
        for (var index = 0; index < steps.Length; index++)
        {
            var (title, minutes, offset, done) = steps[index];
            var day = offset is { } days ? today.AddDays(days) : (DateOnly?)null;
            var step = new ProjectStep
            {
                ProjectId = project.Id,
                Title = title,
                DurationMinutes = minutes,
                PlannedDate = day,
                SortOrder = index,
                IsDone = done,
                CompletedAtUtc = done && day is { } finished ? Local(finished, 18).ToUniversalTime() : (DateTimeOffset?)null
            };
            await projects.AddStepAsync(step, cancellationToken);
            added.Add(step);
        }

        return added;
    }

    private static IReadOnlyList<TaskItem> BuildTasks(DateOnly today) =>
    [
        Task("Draft the quarterly report", 90, 80, today.AddDays(1), 18, PreferredTimeOfDay.Morning),
        Task("Renew the domain", 15, 95, today.AddDays(-3), 12, PreferredTimeOfDay.Any),
        Task("Reply to the design review", 30, 70, today.AddDays(-1), 17, PreferredTimeOfDay.Afternoon),
        Task("Sketch the onboarding flow", 120, 55, null, 0, PreferredTimeOfDay.Morning),
        Task("Book the dentist", 10, 40, today.AddDays(9), 12, PreferredTimeOfDay.Any),
        Task("Read the platform migration notes", 60, 35, null, 0, PreferredTimeOfDay.Evening),
        Task("Plan the offsite agenda", 45, 65, today.AddDays(4), 15, PreferredTimeOfDay.Afternoon),
        Task("Archive last quarter's notes", 25, 20, null, 0, PreferredTimeOfDay.Any),
        Done("Update the dependency lockfile", 30),
        Done("Send the invoice", 15)
    ];

    private static TaskItem Task(string title, int minutes, int priority, DateOnly? deadline, int deadlineHour,
        PreferredTimeOfDay preferred) => new()
    {
        Title = title,
        Status = DomainTaskStatus.Ready,
        Priority = priority,
        EstimatedDurationMin = minutes,
        RemainingDurationMin = minutes,
        MinBlockMin = Math.Min(30, minutes),
        PreferredTimeOfDay = preferred,
        DeadlineUtc = deadline is { } date ? Local(date, deadlineHour).ToUniversalTime() : null
    };

    private static TaskItem Done(string title, int minutes) => new()
    {
        Title = title,
        Status = DomainTaskStatus.Done,
        Priority = 50,
        EstimatedDurationMin = minutes,
        RemainingDurationMin = 0,
        MinBlockMin = Math.Min(30, minutes)
    };

    private static readonly string[] Meetings =
        ["Design review", "1:1", "Roadmap sync", "Customer call", "Retro", "Interview"];

    private static string PickMeeting(Random random) => Meetings[random.Next(Meetings.Length)];

    private static bool Chance(Random random, double probability) => random.NextDouble() < probability;

    private static CalendarEvent Event(DateOnly date, int hour, int minutes, string title,
        EventCategory category, EventPriority priority = EventPriority.Normal)
    {
        var start = Local(date, hour);
        return new CalendarEvent
        {
            Title = title,
            StartUtc = start.ToUniversalTime(),
            EndUtc = start.AddMinutes(minutes).ToUniversalTime(),
            Category = category,
            Priority = priority,
            Source = SourceMarker
        };
    }

    private static DateTimeOffset Local(DateOnly date, int hour)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddHours(hour), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
