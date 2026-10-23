using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// The capabilities added so the assistant can act on a whole request in one pass rather than
/// one round trip per item: finding things by name, booking a set of slots together, moving a
/// task through its lifecycle, and reserving time for one.
/// </summary>
public sealed class AssistantNewToolsTests
{
    private static AssistantToolInvocation Call(string name, string arguments = "{}")
        => new("call_1", name, arguments);

    private static async Task<AppSettings> FullAccessAsync(AssistantTestHarness harness, CancellationToken cancellationToken)
        => await harness.ConfigureAsync(settings =>
        {
            settings.AssistantAccessLevel = AssistantAccessLevel.FullAccess;
            settings.ShareContextWithExternalAi = true;
        }, cancellationToken);

    [Fact]
    public async Task SearchSchedule_FindsAnEventByPartOfItsNameAtAnyDistance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var (far, _) = ScheduleActionExecutor.DayRange(DateOnly.FromDateTime(DateTime.Today).AddDays(70));
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = "Dentist appointment",
            StartUtc = far.AddHours(11),
            EndUtc = far.AddHours(12)
        }, cancellationToken);
        await harness.Store.AddAsync(new TaskItem { Title = "Buy dental floss" }, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(Call("search_schedule", """{"query":"dent"}"""), cancellationToken);

        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        var found = Assert.Single(json.RootElement.GetProperty("events").EnumerateArray().ToList());
        Assert.Equal("Dentist appointment", found.GetProperty("title").GetString());
        Assert.True(Guid.TryParse(found.GetProperty("id").GetString(), out _));
        Assert.Single(json.RootElement.GetProperty("tasks").EnumerateArray().ToList());
    }

    [Fact]
    public async Task ReadTools_ShowTheStepsATaskOrAnEventIsBrokenInto()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await FullAccessAsync(harness, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(today);

        var launch = new TaskItem { Title = "Launch the website" };
        await harness.Store.AddAsync(launch, cancellationToken);
        var website = new Project { Title = launch.Title, TaskId = launch.Id };
        await harness.Projects.AddProjectAsync(website, cancellationToken);
        var slot = new CalendarEvent { Title = "Design the landing page", StartUtc = dayStart.AddHours(14), EndUtc = dayStart.AddHours(16) };
        await harness.Store.AddAsync(slot, cancellationToken);
        await harness.Projects.AddStepsAsync([
            new ProjectStep { ProjectId = website.Id, Title = "Wireframe", DurationMinutes = 90, IsDone = true, SortOrder = 0 },
            new ProjectStep { ProjectId = website.Id, Title = "Design the landing page", DurationMinutes = 120, PlannedDate = today, EventId = slot.Id, SortOrder = 1 }
        ], cancellationToken);

        var deepWork = new CalendarEvent { Title = "Deep work", StartUtc = dayStart.AddHours(9), EndUtc = dayStart.AddHours(11) };
        await harness.Store.AddAsync(deepWork, cancellationToken);
        var sitting = new Project { Title = deepWork.Title, EventId = deepWork.Id };
        await harness.Projects.AddProjectAsync(sitting, cancellationToken);
        var bug = new ProjectStep { ProjectId = sitting.Id, Title = "Reproduce the bug", DurationMinutes = 20, SortOrder = 0 };
        var fix = new ProjectStep { ProjectId = sitting.Id, Title = "Fix it", DurationMinutes = 45, SortOrder = 1 };
        await harness.Projects.AddStepsAsync([bug, fix], cancellationToken);
        await harness.Projects.CheckAsync(deepWork.Id, [bug.Id], DateTimeOffset.UtcNow, cancellationToken);

        var tasks = await harness.Toolbox.InvokeAsync(Call("get_tasks"), cancellationToken);
        using (var json = JsonDocument.Parse(tasks.Content))
        {
            var task = Assert.Single(json.RootElement.GetProperty("tasks").EnumerateArray().ToList());
            // In the order they are worked through: by day, the undated last.
            var steps = task.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(new[] { "Design the landing page", "Wireframe" }, steps.Select(step => step.GetProperty("title").GetString()));
            Assert.Equal(today.ToString("yyyy-MM-dd"), steps[0].GetProperty("day").GetString());
            Assert.True(steps[0].GetProperty("booked").GetBoolean());
            Assert.True(steps[1].GetProperty("done").GetBoolean());
        }

        var schedule = await harness.Toolbox.InvokeAsync(Call("get_schedule"), cancellationToken);
        using (var json = JsonDocument.Parse(schedule.Content))
        {
            var events = json.RootElement.GetProperty("events").EnumerateArray().ToList();
            var checklist = events.Single(item => item.GetProperty("title").GetString() == "Deep work").GetProperty("steps")
                .EnumerateArray().ToList();
            Assert.Equal(new[] { true, false }, checklist.Select(step => step.GetProperty("done").GetBoolean()));
            var booked = events.Single(item => item.GetProperty("title").GetString() == "Design the landing page");
            Assert.Equal("Launch the website", booked.GetProperty("projectStep").GetProperty("task").GetString());
        }
    }

    [Fact]
    public async Task SearchSchedule_SaysSoWhenNothingMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(Call("search_schedule", """{"query":"nothing here"}"""), cancellationToken);

        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Empty(json.RootElement.GetProperty("events").EnumerateArray());
        Assert.Contains("Nothing matched", json.RootElement.GetProperty("note").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateEvents_BooksTheWholeBatchInOneCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await FullAccessAsync(harness, cancellationToken);
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(2);

        var result = await harness.Toolbox.InvokeAsync(Call("create_events", $$"""
            {"events":[
              {"title":"Deep work","startLocal":"{{day:yyyy-MM-dd}}T09:00:00","durationMinutes":90},
              {"title":"Review","startLocal":"{{day:yyyy-MM-dd}}T14:00:00","endLocal":"{{day:yyyy-MM-dd}}T15:00:00"},
              {"title":"Gym","startLocal":"{{day:yyyy-MM-dd}}T18:00:00","durationMinutes":60}]}
            """), cancellationToken);

        Assert.False(result.IsError);
        Assert.True(result.Applied);
        var (start, end) = ScheduleActionExecutor.DayRange(day);
        var stored = await harness.Store.GetRangeAsync(start, end, cancellationToken);
        Assert.Equal(3, stored.Count);
        Assert.Equal(["Deep work", "Review", "Gym"], stored.OrderBy(item => item.StartUtc).Select(item => item.Title));
    }

    [Fact]
    public async Task CreateEvents_UnderConfirmAll_ProposesEveryEntryRatherThanTheFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(2);

        var result = await harness.Toolbox.InvokeAsync(Call("create_events", $$"""
            {"events":[
              {"title":"One","startLocal":"{{day:yyyy-MM-dd}}T09:00:00"},
              {"title":"Two","startLocal":"{{day:yyyy-MM-dd}}T11:00:00"}]}
            """), cancellationToken);

        Assert.False(result.Applied);
        Assert.Equal(2, result.Proposals.Count);
        Assert.All(result.Proposals, action => Assert.Equal(AssistantActionKind.CreateEvent, action.Kind));
        Assert.Contains("NOT saved", result.Content, StringComparison.OrdinalIgnoreCase);
        var (start, end) = ScheduleActionExecutor.DayRange(day);
        Assert.Empty(await harness.Store.GetRangeAsync(start, end, cancellationToken));
    }

    [Fact]
    public async Task CreateEvents_RejectsEntriesWithNothingUsableInThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await FullAccessAsync(harness, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("create_events", """{"events":[{"title":"No time given"}]}"""), cancellationToken);

        Assert.True(result.IsError);
        Assert.Empty(await harness.Store.GetAllAsync(cancellationToken));
    }

    [Fact]
    public async Task SetTaskStatus_CompletesATaskWithoutDestroyingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await FullAccessAsync(harness, cancellationToken);
        var task = new TaskItem { Title = "Write the report", EstimatedDurationMin = 60, RemainingDurationMin = 60 };
        await harness.Store.AddAsync(task, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("set_task_status", $$"""{"targetId":"{{task.Id}}","status":"Done"}"""), cancellationToken);

        Assert.False(result.IsError);
        Assert.True(result.Applied);
        var stored = Assert.Single(await harness.Store.GetAllAsync(cancellationToken));
        Assert.Equal(DomainTaskStatus.Done, stored.Status);
        Assert.Equal(0, stored.RemainingDurationMin);
    }

    [Fact]
    public async Task SetTaskStatus_RefusesAnIdThatIsNoLongerThere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await FullAccessAsync(harness, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("set_task_status", $$"""{"targetId":"{{Guid.NewGuid()}}","status":"Done"}"""), cancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("get_tasks", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFocusBlock_ReservesTimeAndMarksTheTaskScheduled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await FullAccessAsync(harness, cancellationToken);
        var task = new TaskItem { Title = "Draft the proposal", EstimatedDurationMin = 90, RemainingDurationMin = 90 };
        await harness.Store.AddAsync(task, cancellationToken);
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(1);

        var result = await harness.Toolbox.InvokeAsync(Call("create_focus_block",
            $$"""{"targetId":"{{task.Id}}","startLocal":"{{day:yyyy-MM-dd}}T10:00:00","durationMinutes":90}"""),
            cancellationToken);

        Assert.False(result.IsError);
        Assert.True(result.Applied);
        var (start, end) = ScheduleActionExecutor.DayRange(day);
        var block = Assert.Single(await ((IBlockRepository)harness.Store).GetRangeAsync(start, end, cancellationToken));
        Assert.Equal(task.Id, block.TaskId);
        Assert.Equal(90, block.DurationMinutes);
        var stored = Assert.Single(await harness.Store.GetAllAsync(cancellationToken));
        Assert.Equal(DomainTaskStatus.Scheduled, stored.Status);
    }

    /// <summary>
    /// The read tools are the ones a turn asks for several of at once, and the loop answers
    /// those together. Anything that writes must stay outside that set.
    /// </summary>
    [Fact]
    public void OnlyLookingToolsAreMarkedSafeToRunTogether()
    {
        Assert.True(ScheduleToolbox.IsReadOnly("get_schedule"));
        Assert.True(ScheduleToolbox.IsReadOnly("search_schedule"));
        Assert.True(ScheduleToolbox.IsReadOnly("find_free_slots"));
        foreach (var tool in ScheduleToolbox.Definitions.Where(item => item.IsMutating))
            Assert.False(ScheduleToolbox.IsReadOnly(tool.Name), $"{tool.Name} writes and must not run concurrently");
    }
}
