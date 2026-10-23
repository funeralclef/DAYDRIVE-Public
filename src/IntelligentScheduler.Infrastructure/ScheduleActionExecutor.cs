using System.Globalization;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// The single place where an approved assistant action turns into a database change.
/// Both the chat tool loop and the confirm buttons in the UI go through here, so the
/// rules for what a change means cannot drift apart between them.
/// </summary>
public sealed class ScheduleActionExecutor(ITaskRepository tasks, ICalendarRepository events, IBlockRepository blocks)
    : IScheduleActionExecutor
{
    public async Task<string> ApplyAsync(AssistantScheduleAction action, CancellationToken cancellationToken = default)
        => action.Kind switch
        {
            AssistantActionKind.CreateEvent => await CreateEventAsync(action, cancellationToken),
            AssistantActionKind.UpdateEvent => await UpdateEventAsync(action, cancellationToken),
            AssistantActionKind.DeleteEvent => await DeleteEventAsync(action, cancellationToken),
            AssistantActionKind.CopyEvent => await CopyEventAsync(action, cancellationToken),
            AssistantActionKind.CreateTask => await CreateTaskAsync(action, cancellationToken),
            AssistantActionKind.UpdateTask => await UpdateTaskAsync(action, cancellationToken),
            AssistantActionKind.DeleteTask => await DeleteTaskAsync(action, cancellationToken),
            AssistantActionKind.SetTaskStatus => await SetTaskStatusAsync(action, cancellationToken),
            AssistantActionKind.CreateFocusBlock => await CreateFocusBlockAsync(action, cancellationToken),
            AssistantActionKind.ShiftDay => await ShiftDayAsync(action, cancellationToken),
            AssistantActionKind.CopyDay => await CopyDayAsync(action, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported action.")
        };

    private async Task<string> CreateEventAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            throw new InvalidOperationException("An event needs a title.");
        if (action.StartLocal is not { } start)
            throw new InvalidOperationException("An event needs startLocal.");

        var end = action.EndLocal
                  ?? start.AddMinutes(Math.Clamp(action.DurationMinutes ?? 60, 1, 10_080));
        if (end <= start) throw new InvalidOperationException("endLocal must be after startLocal.");

        var calendarEvent = new CalendarEvent
        {
            Title = action.Title.Trim(),
            StartUtc = start.ToUniversalTime(),
            EndUtc = end.ToUniversalTime(),
            Category = action.Category ?? EventCategory.General,
            Priority = ToEventPriority(action.Priority),
            Source = "Assistant"
        };
        await events.AddAsync(calendarEvent, cancellationToken);
        return $"Created event {calendarEvent.Id} \"{calendarEvent.Title}\" {Describe(start, end)}.";
    }

    private async Task<string> UpdateEventAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var calendarEvent = await FindEventAsync(action.TargetId, cancellationToken);
        var originalDuration = calendarEvent.EndUtc - calendarEvent.StartUtc;
        if (!string.IsNullOrWhiteSpace(action.Title)) calendarEvent.Title = action.Title.Trim();
        if (action.StartLocal is { } start)
        {
            calendarEvent.StartUtc = start.ToUniversalTime();
            // Moving an event without a new end keeps its length instead of collapsing it.
            if (action.EndLocal is null) calendarEvent.EndUtc = calendarEvent.StartUtc + originalDuration;
        }
        if (action.EndLocal is { } end) calendarEvent.EndUtc = end.ToUniversalTime();
        if (action.EndLocal is null && action.DurationMinutes is { } minutes && action.StartLocal is not null)
            calendarEvent.EndUtc = calendarEvent.StartUtc.AddMinutes(Math.Clamp(minutes, 1, 10_080));
        if (calendarEvent.EndUtc <= calendarEvent.StartUtc)
            throw new InvalidOperationException("The updated event would end before it starts.");
        if (action.Category is { } category) calendarEvent.Category = category;
        if (action.Priority is not null) calendarEvent.Priority = ToEventPriority(action.Priority);
        // Editing one occurrence of a series must not rewrite the whole series.
        calendarEvent.IsRecurrenceException = calendarEvent.SeriesId.HasValue;
        await events.UpdateAsync(calendarEvent, cancellationToken);
        return $"Updated event {calendarEvent.Id} \"{calendarEvent.Title}\" " +
               $"{Describe(calendarEvent.StartUtc.ToLocalTime(), calendarEvent.EndUtc.ToLocalTime())}.";
    }

    private async Task<string> DeleteEventAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var calendarEvent = await FindEventAsync(action.TargetId, cancellationToken);
        await events.DeleteScopeAsync(calendarEvent, EventSeriesScope.Occurrence, cancellationToken);
        return $"Deleted event \"{calendarEvent.Title}\".";
    }

    private async Task<string> CopyEventAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var source = await FindEventAsync(action.TargetId, cancellationToken);
        if (action.TargetDate is not { } targetDate)
            throw new InvalidOperationException("copy_event needs targetDate.");
        var sourceDate = DateOnly.FromDateTime(source.StartUtc.ToLocalTime().Date);
        var copy = CopyEventToDay(source, targetDate.DayNumber - sourceDate.DayNumber);
        await events.AddAsync(copy, cancellationToken);
        return $"Copied \"{source.Title}\" to {targetDate:yyyy-MM-dd}.";
    }

    private async Task<string> CreateTaskAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            throw new InvalidOperationException("A task needs a title.");
        var duration = Math.Clamp(action.DurationMinutes ?? 30, 1, 10_080);
        var task = new TaskItem
        {
            Title = action.Title.Trim(),
            Status = DomainTaskStatus.Ready,
            Priority = Math.Clamp(action.Priority ?? 50, 0, 100),
            EstimatedDurationMin = duration,
            RemainingDurationMin = duration,
            MinBlockMin = Math.Min(30, duration),
            DeadlineUtc = action.DeadlineLocal?.ToUniversalTime(),
            PreferredTimeOfDay = action.PreferredTime ?? PreferredTimeOfDay.Any
        };
        await tasks.AddAsync(task, cancellationToken);
        return $"Created task {task.Id} \"{task.Title}\" ({duration} min). It stays unscheduled until an event is created for it.";
    }

    private async Task<string> UpdateTaskAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var task = await FindTaskAsync(action.TargetId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(action.Title)) task.Title = action.Title.Trim();
        if (action.Priority is { } priority) task.Priority = Math.Clamp(priority, 0, 100);
        if (action.DeadlineLocal is { } deadline) task.DeadlineUtc = deadline.ToUniversalTime();
        if (action.PreferredTime is { } preferred) task.PreferredTimeOfDay = preferred;
        if (action.DurationMinutes is { } duration)
        {
            duration = Math.Clamp(duration, 1, 10_080);
            task.EstimatedDurationMin = duration;
            task.RemainingDurationMin = Math.Min(task.RemainingDurationMin, duration);
            if (task.RemainingDurationMin == 0) task.RemainingDurationMin = duration;
            task.MinBlockMin = Math.Min(task.MinBlockMin, duration);
        }
        await tasks.UpdateAsync(task, cancellationToken);
        return $"Updated task {task.Id} \"{task.Title}\".";
    }

    private async Task<string> DeleteTaskAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var task = await FindTaskAsync(action.TargetId, cancellationToken);
        await tasks.DeleteAsync(task.Id, cancellationToken);
        return $"Deleted task \"{task.Title}\".";
    }

    /// <summary>
    /// Moves a task through its lifecycle. Finishing one also clears what is left of it, so the
    /// planner stops offering to schedule work that is already done.
    /// </summary>
    private async Task<string> SetTaskStatusAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var task = await FindTaskAsync(action.TargetId, cancellationToken);
        if (action.TaskStatus is not { } status)
            throw new InvalidOperationException("set_task_status needs a status.");

        task.Status = status;
        if (status is DomainTaskStatus.Done or DomainTaskStatus.Cancelled) task.RemainingDurationMin = 0;
        else if (task.RemainingDurationMin == 0) task.RemainingDurationMin = task.EstimatedDurationMin;
        await tasks.UpdateAsync(task, cancellationToken);
        return $"Task \"{task.Title}\" is now {status}.";
    }

    /// <summary>
    /// Reserves working time for a task without turning it into a calendar event. This is the
    /// app's own distinction: an event is an appointment, a focus block is time set aside to do
    /// something that is already on the task list.
    /// </summary>
    private async Task<string> CreateFocusBlockAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var task = await FindTaskAsync(action.TargetId, cancellationToken);
        if (action.StartLocal is not { } start)
            throw new InvalidOperationException("A focus block needs startLocal.");
        var end = action.EndLocal
                  ?? start.AddMinutes(Math.Clamp(action.DurationMinutes ?? task.RemainingDurationMin, 5, 10_080));
        if (end <= start) throw new InvalidOperationException("endLocal must be after startLocal.");

        await blocks.AddAsync(new TimeBlock
        {
            TaskId = task.Id,
            TitleOverride = task.Title,
            StartUtc = start.ToUniversalTime(),
            EndUtc = end.ToUniversalTime(),
            State = BlockState.Planned,
            Origin = BlockOrigin.Manual
        }, cancellationToken);

        if (task.Status is DomainTaskStatus.Inbox or DomainTaskStatus.Ready)
        {
            task.Status = DomainTaskStatus.Scheduled;
            await tasks.UpdateAsync(task, cancellationToken);
        }
        return $"Reserved {Describe(start, end)} to work on \"{task.Title}\".";
    }

    private async Task<string> ShiftDayAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        if (action.SourceDate is not { } date)
            throw new InvalidOperationException("shift_day needs sourceDate.");
        if (action.OffsetMinutes is not { } offset || offset == 0)
            throw new InvalidOperationException("shift_day needs a non-zero offsetMinutes.");
        var (start, end) = DayRange(date);
        var moved = await events.ShiftRangeAsync(start, end, TimeSpan.FromMinutes(offset), cancellationToken);
        return $"Shifted {moved} event(s) on {date:yyyy-MM-dd} by {offset} minutes.";
    }

    private async Task<string> CopyDayAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        if (action.SourceDate is not { } source || action.TargetDate is not { } target)
            throw new InvalidOperationException("copy_day needs sourceDate and targetDate.");
        var (start, end) = DayRange(source);
        var sourceEvents = await events.GetRangeAsync(start, end, cancellationToken);
        var offset = target.DayNumber - source.DayNumber;
        var copies = sourceEvents.Select(item => CopyEventToDay(item, offset)).ToArray();
        await events.AddRangeAsync(copies, cancellationToken);
        return $"Copied {copies.Length} event(s) from {source:yyyy-MM-dd} to {target:yyyy-MM-dd}.";
    }

    private async Task<CalendarEvent> FindEventAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is null) throw new InvalidOperationException("This action needs the targetId of an existing event.");
        return (await events.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == id.Value)
               ?? throw new InvalidOperationException($"The event {id} no longer exists. Re-read the schedule before retrying.");
    }

    private async Task<TaskItem> FindTaskAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is null) throw new InvalidOperationException("This action needs the targetId of an existing task.");
        return (await tasks.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == id.Value)
               ?? throw new InvalidOperationException($"The task {id} no longer exists. Re-read the task list before retrying.");
    }

    public static CalendarEvent CopyEventToDay(CalendarEvent source, int offsetDays) => new()
    {
        Title = source.Title,
        StartUtc = ShiftDays(source.StartUtc, offsetDays),
        EndUtc = ShiftDays(source.EndUtc, offsetDays),
        Source = "Local",
        Category = source.Category,
        Priority = source.Priority,
        Flexibility = source.Flexibility,
        Recurrence = EventRecurrence.None
    };

    private static DateTimeOffset ShiftDays(DateTimeOffset value, int days)
    {
        // Shifting in local time keeps the wall clock intact across a DST boundary.
        var local = DateTime.SpecifyKind(value.ToLocalTime().LocalDateTime.AddDays(days), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    public static (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly date)
        => LocalDateRange.Utc(date);

    private static EventPriority ToEventPriority(int? priority) => priority switch
    {
        >= 90 => EventPriority.Critical,
        >= 65 => EventPriority.High,
        _ => EventPriority.Normal
    };

    private static string Describe(DateTimeOffset start, DateTimeOffset end)
        => $"{start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}–{end.ToString("HH:mm", CultureInfo.InvariantCulture)}";
}
