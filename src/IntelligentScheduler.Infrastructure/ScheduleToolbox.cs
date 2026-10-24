using System.Globalization;
using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// The tools DAYDRIVE itself offers to the language model. Read tools run immediately so the
/// model can ground itself in the real schedule; write tools are filtered through the access
/// level, and anything the user still has to approve is returned as a pending action instead
/// of being silently applied.
/// </summary>
public sealed class ScheduleToolbox(
    ITaskRepository tasks,
    ICalendarRepository events,
    IBlockRepository blocks,
    ITaskExecutionRepository executions,
    IEventOutcomeRepository outcomes,
    IUserMemoryRepository memories,
    ISettingsRepository settings,
    IScheduleActionExecutor executor,
    IProjectRepository? projects = null) : IAssistantToolbox
{
    private const int MaxListedItems = 120;

    /// <summary>Tools that hand real schedule data to the model.</summary>
    private static readonly string[] ContextReadingTools =
        ["get_schedule", "get_tasks", "search_schedule", "find_free_slots", "get_analytics", "list_memories"];

    /// <summary>
    /// Tools that only look. Nothing they do can affect another call, so a turn that asks for
    /// several of them has them answered at the same time rather than one after another.
    /// </summary>
    public static bool IsReadOnly(string toolName) => ContextReadingTools.Contains(toolName);

    public async ValueTask<IReadOnlyList<AssistantToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        // Without consent the reading tools are not even offered, so the model does not plan around them.
        return MayShareContext(await settings.GetAppSettingsAsync(cancellationToken))
            ? Definitions
            : [.. Definitions.Where(tool => !ContextReadingTools.Contains(tool.Name))];
    }

    /// <summary>The local model always sees the schedule; anything external needs the consent flag.</summary>
    public static bool MayShareContext(AppSettings current)
        => current.AssistantProvider == AssistantProvider.Local || current.ShareContextWithExternalAi;

    public async ValueTask<AssistantToolResult> InvokeAsync(
        AssistantToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var arguments = AssistantToolJson.Parse(invocation.ArgumentsJson);
        try
        {
            // Consent is enforced here rather than only when building the tool list, so a model
            // that calls a withheld tool by name still cannot read the user's schedule.
            if (ContextReadingTools.Contains(invocation.Name) &&
                !MayShareContext(await settings.GetAppSettingsAsync(cancellationToken)))
                return new(invocation.CallId, invocation.Name,
                    "Context sharing with an external model is switched off in settings, so this data is unavailable. " +
                    "Tell the user they can enable it if they want you to see their schedule.", IsError: true);

            return invocation.Name switch
            {
                "get_schedule" => Ok(invocation, await GetScheduleAsync(arguments, cancellationToken)),
                "get_tasks" => Ok(invocation, await GetTasksAsync(arguments, cancellationToken)),
                "search_schedule" => Ok(invocation, await SearchScheduleAsync(arguments, cancellationToken)),
                "find_free_slots" => Ok(invocation, await FindFreeSlotsAsync(arguments, cancellationToken)),
                "get_analytics" => Ok(invocation, await GetAnalyticsAsync(arguments, cancellationToken)),
                "list_memories" => Ok(invocation, await ListMemoriesAsync(cancellationToken)),
                "remember_about_user" => await RememberAsync(invocation, arguments, cancellationToken),
                "forget_about_user" => await ForgetAsync(invocation, arguments, cancellationToken),
                "create_events" => await CreateEventsAsync(invocation, arguments, cancellationToken),
                _ => await MutateAsync(invocation, arguments, cancellationToken)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            // The model recovers far better from a readable failure than from a dropped turn.
            return new(invocation.CallId, invocation.Name, $"Error: {exception.Message}", IsError: true);
        }
    }

    private static AssistantToolResult Ok(AssistantToolInvocation invocation, string content)
        => new(invocation.CallId, invocation.Name, content);

    // ---------------------------------------------------------------- read tools

    private async Task<string> GetScheduleAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var from = AssistantToolJson.Date(arguments, "fromDate") ?? DateOnly.FromDateTime(now.LocalDateTime);
        var to = AssistantToolJson.Date(arguments, "toDate") ?? from.AddDays(6);
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 120) to = from.AddDays(120);

        var (start, _) = ScheduleActionExecutor.DayRange(from);
        var (_, end) = ScheduleActionExecutor.DayRange(to);
        var found = await events.GetRangeAsync(start, end, cancellationToken);
        var focus = await blocks.GetRangeAsync(start, end, cancellationToken);
        var lens = await ReadProjectsAsync(cancellationToken);
        var listed = found.Take(MaxListedItems).ToList();
        var ticks = projects is null || (lens.ByEvent.Count == 0 && lens.BySeries.Count == 0)
            ? []
            : (await projects.GetChecksAsync([.. listed.Select(item => item.Id)], cancellationToken))
                .Select(check => (check.EventId, check.StepId)).ToHashSet();
        var taskTitles = lens.Slots.Count == 0
            ? new Dictionary<Guid, string>()
            : (await tasks.GetAllAsync(cancellationToken)).ToDictionary(task => task.Id, task => task.Title);

        return AssistantToolJson.Serialize(new
        {
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            timeZone = TimeZoneInfo.Local.Id,
            events = listed.Select(item =>
            {
                var basic = new
                {
                    id = item.Id,
                    title = item.Title,
                    start = Local(item.StartUtc),
                    end = Local(item.EndUtc),
                    minutes = (int)(item.EndUtc - item.StartUtc).TotalMinutes,
                    category = item.Category.ToString(),
                    priority = item.Priority.ToString(),
                    recurring = item.SeriesId is not null
                };
                // An event broken into steps: its checklist, ticked for this occurrence.
                var sitting = lens.ByEvent.GetValueOrDefault(item.Id)
                              ?? (item.SeriesId is { } series ? lens.BySeries.GetValueOrDefault(series) : null);
                if (sitting is not null)
                    return (object)new
                    {
                        basic.id, basic.title, basic.start, basic.end, basic.minutes, basic.category, basic.priority, basic.recurring,
                        steps = sitting.Select(step => new
                        {
                            title = step.Title,
                            minutes = step.DurationMinutes,
                            done = ticks.Contains((item.Id, step.Id))
                        })
                    };
                // The hour booked for one step of an unscheduled project.
                if (lens.Slots.TryGetValue(item.Id, out var slot))
                    return new
                    {
                        basic.id, basic.title, basic.start, basic.end, basic.minutes, basic.category, basic.priority, basic.recurring,
                        projectStep = new { task = taskTitles.GetValueOrDefault(slot.TaskId), taskId = slot.TaskId, step = slot.Step.Title }
                    };
                return basic;
            }),
            focusBlocks = focus.Where(block => block.State != BlockState.Cancelled).Take(MaxListedItems).Select(block => new
            {
                id = block.Id,
                title = block.Task?.Title ?? block.TitleOverride,
                start = Local(block.StartUtc),
                end = Local(block.EndUtc),
                state = block.State.ToString()
            })
        });
    }

    private async Task<string> GetTasksAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var includeDone = AssistantToolJson.Bool(arguments, "includeDone") ?? false;
        var all = await tasks.GetAllAsync(cancellationToken);
        var filtered = includeDone
            ? all
            : all.Where(task => task.Status is not DomainTaskStatus.Done and not DomainTaskStatus.Cancelled).ToList();
        var lens = await ReadProjectsAsync(cancellationToken);
        return AssistantToolJson.Serialize(new
        {
            count = filtered.Count,
            note = "Tasks are intentions without a time slot. Creating an event is what puts one on the calendar. " +
                   "A task with steps is a project broken into them: its minutes are what its open steps add up to, " +
                   "a step's day is a plan rather than a booking, and a booked step already has an event of its own.",
            tasks = filtered.Take(MaxListedItems).Select(task =>
            {
                var basic = new
                {
                    id = task.Id,
                    title = task.Title,
                    status = task.Status.ToString(),
                    priority = task.Priority,
                    minutes = task.RemainingDurationMin,
                    deadline = task.DeadlineUtc is { } deadline ? Local(deadline) : null,
                    preferredTime = task.PreferredTimeOfDay.ToString(),
                    notes = task.Notes
                };
                if (!lens.ByTask.TryGetValue(task.Id, out var steps) || steps.Count == 0) return (object)basic;
                return new
                {
                    basic.id, basic.title, basic.status, basic.priority, basic.minutes, basic.deadline, basic.preferredTime, basic.notes,
                    steps = steps.Select(step => new
                    {
                        title = step.Title,
                        minutes = step.DurationMinutes,
                        day = step.PlannedDate?.ToString("yyyy-MM-dd"),
                        done = step.IsDone,
                        booked = step.EventId is not null
                    })
                };
            })
        });
    }

    /// <summary>
    /// Finds things by name across the whole calendar and task list.
    ///
    /// Without this, acting on "move the dentist appointment" meant guessing a date range and
    /// paging through it, which is several round trips for something the database answers in
    /// one — and gets the wrong answer whenever the thing is further out than the guess.
    /// </summary>
    private async Task<string> SearchScheduleAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = (AssistantToolJson.String(arguments, "query") ?? string.Empty).Trim();
        if (query.Length == 0)
            return AssistantToolJson.Serialize(new { error = "query is required." });

        var includeDone = AssistantToolJson.Bool(arguments, "includePast") ?? true;
        var now = DateTimeOffset.Now;
        var matchedEvents = (await events.GetAllAsync(cancellationToken))
            .Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(item => includeDone || item.EndUtc >= now)
            .OrderBy(item => Math.Abs((item.StartUtc - now).Ticks))
            .Take(25)
            .OrderBy(item => item.StartUtc)
            .ToList();

        var matchedTasks = (await tasks.GetAllAsync(cancellationToken))
            .Where(task => task.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || (task.Notes ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .ToList();

        return AssistantToolJson.Serialize(new
        {
            query,
            events = matchedEvents.Select(item => new
            {
                id = item.Id,
                title = item.Title,
                start = Local(item.StartUtc),
                end = Local(item.EndUtc),
                category = item.Category.ToString(),
                isPast = item.EndUtc < now,
                recurring = item.SeriesId is not null
            }),
            tasks = matchedTasks.Select(task => new
            {
                id = task.Id,
                title = task.Title,
                status = task.Status.ToString(),
                priority = task.Priority,
                minutes = task.RemainingDurationMin,
                deadline = task.DeadlineUtc is { } deadline ? Local(deadline) : null
            }),
            note = matchedEvents.Count == 0 && matchedTasks.Count == 0
                ? "Nothing matched. The search is a plain substring match on titles, so try a shorter fragment."
                : "Matches are ordered by date. Use these ids directly."
        });
    }

    private async Task<string> FindFreeSlotsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var from = AssistantToolJson.Date(arguments, "fromDate") ?? DateOnly.FromDateTime(now.LocalDateTime);
        var days = Math.Clamp(AssistantToolJson.Int(arguments, "days") ?? 1, 1, 21);
        var duration = Math.Clamp(AssistantToolJson.Int(arguments, "durationMinutes") ?? 60, 5, 12 * 60);
        var earliestHour = Math.Clamp(AssistantToolJson.Int(arguments, "earliestHour") ?? 8, 0, 23);
        var latestHour = Math.Clamp(AssistantToolJson.Int(arguments, "latestHour") ?? 22, 1, 24);
        if (latestHour <= earliestHour) latestHour = Math.Min(24, earliestHour + 1);

        var (rangeStart, _) = ScheduleActionExecutor.DayRange(from);
        var (_, rangeEnd) = ScheduleActionExecutor.DayRange(from.AddDays(days - 1));
        var busy = (await events.GetRangeAsync(rangeStart, rangeEnd, cancellationToken))
            .Select(item => (item.StartUtc, item.EndUtc))
            .Concat((await blocks.GetRangeAsync(rangeStart, rangeEnd, cancellationToken))
                .Where(block => block.State is BlockState.Planned or BlockState.InProgress)
                .Select(block => (block.StartUtc, block.EndUtc)))
            .OrderBy(pair => pair.Item1)
            .ToList();

        var slots = new List<object>();
        for (var offset = 0; offset < days; offset++)
        {
            var date = from.AddDays(offset);
            var dayStart = LocalMoment(date, earliestHour);
            var dayEnd = latestHour >= 24 ? LocalMoment(date.AddDays(1), 0) : LocalMoment(date, latestHour);
            // Never propose a window that has already passed today.
            var cursor = dayStart < now ? RoundUp(now, 15) : dayStart;

            foreach (var (busyStart, busyEnd) in busy
                         .Select(pair => (Start: pair.Item1.ToLocalTime(), End: pair.Item2.ToLocalTime()))
                         .Where(pair => pair.End > cursor && pair.Start < dayEnd)
                         .OrderBy(pair => pair.Start))
            {
                if (busyStart > cursor && (busyStart - cursor).TotalMinutes >= duration)
                    slots.Add(Slot(cursor, busyStart));
                if (busyEnd > cursor) cursor = busyEnd;
            }
            if (dayEnd > cursor && (dayEnd - cursor).TotalMinutes >= duration) slots.Add(Slot(cursor, dayEnd));
            if (slots.Count >= 40) break;
        }

        return AssistantToolJson.Serialize(new
        {
            requestedMinutes = duration,
            window = $"{earliestHour:00}:00–{latestHour:00}:00",
            freeSlots = slots
        });

        object Slot(DateTimeOffset start, DateTimeOffset end) => new
        {
            start = start.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
            end = end.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
            minutes = (int)(end - start).TotalMinutes
        };
    }

    private async Task<string> GetAnalyticsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var requestedDays = Math.Clamp(AssistantToolJson.Int(arguments, "rangeDays") ?? 30, 3, 180);
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var requestedFrom = AssistantToolJson.Date(arguments, "fromDate");
        var requestedTo = AssistantToolJson.Date(arguments, "toDate");

        AnalyticsPeriod period;
        if (requestedFrom is not null || requestedTo is not null)
        {
            var visibleFrom = requestedFrom ?? requestedTo!.Value;
            var visibleTo = requestedTo ?? visibleFrom;
            if (visibleTo < visibleFrom) (visibleFrom, visibleTo) = (visibleTo, visibleFrom);
            if (visibleTo.DayNumber - visibleFrom.DayNumber >= 180) visibleTo = visibleFrom.AddDays(179);
            var visibleDays = visibleTo.DayNumber - visibleFrom.DayNumber + 1;
            var comparisonOffset = visibleFrom.DayOfWeek == DayOfWeek.Monday && visibleDays <= 7 ? 7 : visibleDays;
            period = new(visibleFrom, visibleTo,
                visibleFrom.AddDays(-comparisonOffset), visibleTo.AddDays(-comparisonOffset));
        }
        else
        {
            period = requestedDays switch
            {
                7 => AnalyticsPeriod.ForCalendarWeek(today),
                30 => AnalyticsPeriod.ForCalendarMonths(today, 1),
                91 => AnalyticsPeriod.ForCalendarMonths(today, 3),
                _ => AnalyticsPeriod.Rolling(today, requestedDays)
            };
        }

        var from = LocalDateRange.StartUtc(period.PreviousFrom.AddDays(-7));
        var to = LocalDateRange.StartUtc(period.To.AddDays(1));

        var report = ScheduleAnalytics.Build(new AnalyticsInput(
            now, period.DayCount,
            await events.GetRangeAsync(from, to, cancellationToken),
            await blocks.GetHistoryAsync(from, to, cancellationToken),
            await executions.GetRecentAsync(from, cancellationToken),
            await tasks.GetAllAsync(cancellationToken),
            Outcomes: await outcomes.GetRangeAsync(from, to, cancellationToken),
            Period: period));

        return AssistantToolJson.Serialize(new
        {
            from = report.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = report.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rangeDays = report.RangeDays,
            scheduledHours = Math.Round(report.ScheduledHours.Value, 1),
            focusHours = Math.Round(report.FocusHours.Value, 1),
            completionRate = Math.Round(report.CompletionRate.Value, 2),
            planAccuracy = Math.Round(report.PlanAccuracy.Value, 2),
            onTimeStartRate = Math.Round(report.OnTimeStartRate.Value, 2),
            load = Math.Round(report.Load.Value, 2),
            currentStreakDays = report.CurrentStreakDays,
            busiestHour = report.BusiestHour,
            categories = report.Categories.Select(slice => new { slice.Key, slice.Minutes, share = Math.Round(slice.Share, 2) }),
            busiestWeekdays = report.Weekdays.OrderByDescending(day => day.Minutes).Take(3).Select(day => new { day = day.Day.ToString(), day.Minutes }),
            insights = report.Insights.Select(insight => new
            {
                kind = insight.Kind.ToString(),
                arguments = insight.Arguments,
                confidence = Math.Round(insight.Confidence, 2),
                insight.SampleSize
            }),
            note = "Derived from local history only. An empty or near-zero sample means there is not enough data yet."
        });
    }

    // ---------------------------------------------------------------- memory tools

    private async Task<string> ListMemoriesAsync(CancellationToken cancellationToken)
    {
        var stored = await memories.GetAllAsync(cancellationToken);
        return AssistantToolJson.Serialize(new
        {
            count = stored.Count,
            memories = stored.Where(entry => entry.IsEnabled)
                .Select(entry => new { entry.Key, entry.Value, category = entry.Category.ToString() })
        });
    }

    private async Task<AssistantToolResult> RememberAsync(
        AssistantToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        var current = await settings.GetAppSettingsAsync(cancellationToken);
        if (!current.AssistantMemoryEnabled)
            return new(invocation.CallId, invocation.Name,
                "Memory is switched off in settings. Tell the user they can enable it if they want you to remember this.", IsError: true);

        var key = AssistantToolJson.String(arguments, "key");
        var value = AssistantToolJson.String(arguments, "value");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return new(invocation.CallId, invocation.Name, "Error: both key and value are required.", IsError: true);

        var entry = await memories.UpsertAsync(new UserMemoryEntry
        {
            Key = key.Trim(),
            Value = value.Trim(),
            Category = AssistantToolJson.Enum<UserMemoryCategory>(arguments, "category") ?? UserMemoryCategory.General,
            IsFromAssistant = true
        }, cancellationToken);

        return new(invocation.CallId, invocation.Name,
            $"Remembered \"{entry.Key}\". The user can review or delete it in the chat memory panel.", Applied: true);
    }

    private async Task<AssistantToolResult> ForgetAsync(
        AssistantToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        var key = AssistantToolJson.String(arguments, "key");
        if (string.IsNullOrWhiteSpace(key))
            return new(invocation.CallId, invocation.Name, "Error: key is required.", IsError: true);
        var removed = await memories.DeleteByKeyAsync(key.Trim(), cancellationToken);
        return new(invocation.CallId, invocation.Name,
            removed ? $"Forgot \"{key.Trim()}\"." : $"No memory named \"{key.Trim()}\" was stored.", Applied: removed);
    }

    // ---------------------------------------------------------------- write tools

    /// <summary>
    /// Books several slots in one call.
    ///
    /// Planning a week one <c>create_event</c> at a time costs one full model round trip per
    /// event — the slowest possible way to do the thing the app is for. Every entry goes through
    /// exactly the same permission check as a single creation would.
    /// </summary>
    private async Task<AssistantToolResult> CreateEventsAsync(
        AssistantToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("events", out var list) || list.ValueKind != JsonValueKind.Array)
            return new(invocation.CallId, invocation.Name, "Error: events must be an array.", IsError: true);

        var drafted = new List<AssistantScheduleAction>();
        foreach (var item in list.EnumerateArray().Take(40))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = AssistantToolJson.String(item, "title")?.Trim();
            var start = AssistantToolJson.Moment(item, "startLocal");
            if (string.IsNullOrWhiteSpace(title) || start is null) continue;
            drafted.Add(new AssistantScheduleAction(
                AssistantActionKind.CreateEvent,
                Title: title,
                StartLocal: start,
                EndLocal: AssistantToolJson.Moment(item, "endLocal"),
                DurationMinutes: AssistantToolJson.Int(item, "durationMinutes", 1, 10_080),
                Priority: AssistantToolJson.Int(item, "priority", 0, 100),
                Category: AssistantToolJson.Enum<EventCategory>(item, "category"),
                Reason: AssistantToolJson.String(item, "reason")));
        }

        if (drafted.Count == 0)
            return new(invocation.CallId, invocation.Name,
                "Error: no usable entries. Each one needs at least a title and startLocal.", IsError: true);

        var current = await settings.GetAppSettingsAsync(cancellationToken);
        if (!AssistantPermissionPolicy.MayApplyAutomatically(current.AssistantAccessLevel, drafted[0]))
            return new(invocation.CallId, invocation.Name,
                $"{drafted.Count} event(s) proposed and shown to the user as confirmation cards. They are NOT saved yet — " +
                "say what you prepared and that you are waiting for their confirmation.",
                PendingActions: drafted);

        // One failure must not lose the rest: the user asked for a week, not for the first
        // three days of one.
        var applied = new List<string>();
        var failed = new List<string>();
        foreach (var action in drafted)
        {
            try { applied.Add(await executor.ApplyAsync(action, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { failed.Add($"{action.Title}: {exception.Message}"); }
        }

        var summary = string.Join("\n", applied);
        if (failed.Count > 0)
            summary += $"\nFailed ({failed.Count}): {string.Join("; ", failed)}";
        return new(invocation.CallId, invocation.Name, summary, Applied: applied.Count > 0);
    }

    private async Task<AssistantToolResult> MutateAsync(
        AssistantToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryBuildAction(invocation.Name, arguments, out var action))
            return new(invocation.CallId, invocation.Name, $"Error: unknown tool \"{invocation.Name}\".", IsError: true);

        // Validate the target before proposing anything: telling the model a change is queued
        // when the id no longer exists only produces a confident, wrong answer to the user.
        if (await DescribeMissingTargetAsync(action, cancellationToken) is { } problem)
            return new(invocation.CallId, invocation.Name, $"Error: {problem}", IsError: true);

        var current = await settings.GetAppSettingsAsync(cancellationToken);
        if (!AssistantPermissionPolicy.MayApplyAutomatically(current.AssistantAccessLevel, action))
            return new(invocation.CallId, invocation.Name,
                "Proposed and shown to the user as a confirmation card. It is NOT saved yet — say that you prepared it and are waiting for their confirmation.",
                PendingActions: [action]);

        var summary = await executor.ApplyAsync(action, cancellationToken);
        return new(invocation.CallId, invocation.Name, summary, Applied: true);
    }

    /// <summary>Returns a message when the action points at something that is not there any more.</summary>
    private async Task<string?> DescribeMissingTargetAsync(AssistantScheduleAction action, CancellationToken cancellationToken)
    {
        var needsEvent = action.Kind is AssistantActionKind.UpdateEvent or AssistantActionKind.DeleteEvent or AssistantActionKind.CopyEvent;
        var needsTask = action.Kind is AssistantActionKind.UpdateTask or AssistantActionKind.DeleteTask
            or AssistantActionKind.SetTaskStatus or AssistantActionKind.CreateFocusBlock;
        if (!needsEvent && !needsTask) return null;

        if (action.TargetId is not { } id)
            return needsEvent
                ? "this action needs the targetId of an existing event. Call get_schedule first."
                : "this action needs the targetId of an existing task. Call get_tasks first.";

        if (needsEvent && (await events.GetAllAsync(cancellationToken)).All(item => item.Id != id))
            return $"the event {id} no longer exists. Call get_schedule again and use a current id.";
        if (needsTask && (await tasks.GetAllAsync(cancellationToken)).All(item => item.Id != id))
            return $"the task {id} no longer exists. Call get_tasks again and use a current id.";
        return null;
    }

    private static bool TryBuildAction(string name, JsonElement arguments, out AssistantScheduleAction action)
    {
        var kind = name switch
        {
            "create_event" => AssistantActionKind.CreateEvent,
            "update_event" => AssistantActionKind.UpdateEvent,
            "delete_event" => AssistantActionKind.DeleteEvent,
            "copy_event" => AssistantActionKind.CopyEvent,
            "create_task" => AssistantActionKind.CreateTask,
            "update_task" => AssistantActionKind.UpdateTask,
            "delete_task" => AssistantActionKind.DeleteTask,
            "set_task_status" => AssistantActionKind.SetTaskStatus,
            "create_focus_block" => AssistantActionKind.CreateFocusBlock,
            "shift_day" => AssistantActionKind.ShiftDay,
            "copy_day" => AssistantActionKind.CopyDay,
            _ => (AssistantActionKind?)null
        } ?? default;

        if (!Definitions.Any(tool => tool.Name == name && tool.IsMutating))
        {
            action = new AssistantScheduleAction(kind);
            return false;
        }

        action = new AssistantScheduleAction(
            kind,
            AssistantToolJson.Id(arguments, "targetId"),
            AssistantToolJson.String(arguments, "title")?.Trim(),
            AssistantToolJson.Moment(arguments, "startLocal"),
            AssistantToolJson.Moment(arguments, "endLocal"),
            AssistantToolJson.Int(arguments, "durationMinutes", 1, 10_080),
            AssistantToolJson.Int(arguments, "priority", 0, 100),
            AssistantToolJson.Enum<EventCategory>(arguments, "category"),
            AssistantToolJson.Date(arguments, "sourceDate"),
            AssistantToolJson.Date(arguments, "targetDate"),
            AssistantToolJson.Int(arguments, "offsetMinutes", -43_200, 43_200),
            AssistantToolJson.String(arguments, "reason"),
            AssistantToolJson.Enum<PreferredTimeOfDay>(arguments, "preferredTime"),
            AssistantToolJson.Moment(arguments, "deadlineLocal"),
            AssistantToolJson.Enum<DomainTaskStatus>(arguments, "status"));
        return true;
    }

    /// <summary>The projects, laid out the way the read tools look things up: by task, by event, by series, by booked slot.</summary>
    private sealed record ProjectLens(
        Dictionary<Guid, List<ProjectStep>> ByTask,
        Dictionary<Guid, List<ProjectStep>> ByEvent,
        Dictionary<Guid, List<ProjectStep>> BySeries,
        Dictionary<Guid, (Guid TaskId, ProjectStep Step)> Slots);

    private async Task<ProjectLens> ReadProjectsAsync(CancellationToken cancellationToken)
    {
        var lens = new ProjectLens(new(), new(), new(), new());
        if (projects is null) return lens;
        var steps = (await projects.GetStepsAsync(cancellationToken)).ToLookup(step => step.ProjectId);
        foreach (var project in await projects.GetProjectsAsync(cancellationToken))
        {
            var own = ProjectSchedule.Order(steps[project.Id]).ToList();
            switch (project.Host)
            {
                case ProjectHost.Task:
                    lens.ByTask[project.TaskId!.Value] = own;
                    foreach (var step in own.Where(step => step.EventId is not null))
                        lens.Slots[step.EventId!.Value] = (project.TaskId.Value, step);
                    break;
                case ProjectHost.Event:
                    lens.ByEvent[project.EventId!.Value] = own;
                    break;
                case ProjectHost.Series:
                    lens.BySeries[project.SeriesId!.Value] = own;
                    break;
            }
        }

        return lens;
    }

    // ---------------------------------------------------------------- schemas

    public static IReadOnlyList<AssistantToolDefinition> Definitions { get; } =
    [
        new("get_schedule",
            "Read real calendar events and focus blocks for a date range. Call this before answering anything about the user's day, week or availability. An event broken into steps lists them, ticked for that occurrence; an event booked for a step of a project says which.",
            """
            {"type":"object","properties":{
              "fromDate":{"type":"string","description":"YYYY-MM-DD. Defaults to today."},
              "toDate":{"type":"string","description":"YYYY-MM-DD. Defaults to six days after fromDate."}},
             "required":[]}
            """),
        new("get_tasks",
            "Read the unscheduled task list with ids, durations, priorities and deadlines. A task broken into steps (a project) lists them with their planned days.",
            """
            {"type":"object","properties":{
              "includeDone":{"type":"boolean","description":"Include completed and cancelled tasks."}},
             "required":[]}
            """),
        new("search_schedule",
            "Find events and tasks by a fragment of their name, anywhere in the calendar, past or future. Use this instead of scanning date ranges when the user names something (\"the dentist appointment\", \"standup\") but not when it is.",
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Part of a title. Shorter is safer: it is a plain substring match."},
              "includePast":{"type":"boolean","description":"Include things that have already happened. Default true."}},
             "required":["query"]}
            """),
        new("find_free_slots",
            "Find gaps that are actually free in the calendar. Use it instead of guessing when proposing a time.",
            """
            {"type":"object","properties":{
              "fromDate":{"type":"string","description":"YYYY-MM-DD. Defaults to today."},
              "days":{"type":"integer","description":"How many days to scan, 1-21. Default 1."},
              "durationMinutes":{"type":"integer","description":"Minimum length of a usable gap. Default 60."},
              "earliestHour":{"type":"integer","description":"Earliest local hour to consider, 0-23. Default 8."},
              "latestHour":{"type":"integer","description":"Latest local hour to consider, 1-24. Default 22."}},
             "required":[]}
            """),
        new("get_analytics",
            "Read computed statistics about the user's schedule history: load, completion rate, plan accuracy, busiest hours and explainable insights.",
            """
            {"type":"object","properties":{
              "rangeDays":{"type":"integer","description":"History window in days, 3-180. Default 30. Use 7 for the current calendar week, 30 for the current calendar month, or 91 for the current and two preceding complete calendar months."},
              "fromDate":{"type":"string","description":"Optional exact report start, YYYY-MM-DD."},
              "toDate":{"type":"string","description":"Optional exact report end, YYYY-MM-DD."}},
             "required":[]}
            """),
        new("list_memories", "Read the durable notes stored about this user.", """{"type":"object","properties":{},"required":[]}"""),
        new("remember_about_user",
            "Store one short durable fact about the user (a preference, routine, constraint or goal) so later conversations can use it. Only store things the user stated or clearly implied.",
            """
            {"type":"object","properties":{
              "key":{"type":"string","description":"Short stable identifier, e.g. \"deep-work-window\"."},
              "value":{"type":"string","description":"The fact itself, one sentence."},
              "category":{"type":"string","enum":["General","Preference","Routine","Constraint","Goal","Context"]}},
             "required":["key","value"]}
            """, IsMutating: false),
        new("forget_about_user", "Delete one stored fact by its key.",
            """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""", IsMutating: false),

        new("create_event",
            "Put a real time slot on the calendar. This is the only way something becomes part of the schedule.",
            """
            {"type":"object","properties":{
              "title":{"type":"string"},
              "startLocal":{"type":"string","description":"Local ISO-8601, for example 2026-08-24T15:30:00."},
              "endLocal":{"type":"string","description":"Local ISO-8601. Omit only if durationMinutes is given."},
              "durationMinutes":{"type":"integer"},
              "category":{"type":"string","enum":["General","Work","Personal","Study","Health","Social","Errands","Travel","Rest"]},
              "priority":{"type":"integer","description":"0-100. 65+ is high, 90+ is critical."},
              "reason":{"type":"string","description":"One short line explaining the choice, shown to the user."}},
             "required":["title","startLocal"]}
            """, IsMutating: true),
        new("update_event",
            "Change an existing event. Use an exact id from get_schedule.",
            """
            {"type":"object","properties":{
              "targetId":{"type":"string","description":"Event id from get_schedule."},
              "title":{"type":"string"},
              "startLocal":{"type":"string","description":"Local ISO-8601. Moving without endLocal keeps the original length."},
              "endLocal":{"type":"string"},
              "durationMinutes":{"type":"integer"},
              "category":{"type":"string","enum":["General","Work","Personal","Study","Health","Social","Errands","Travel","Rest"]},
              "priority":{"type":"integer"},
              "reason":{"type":"string"}},
             "required":["targetId"]}
            """, IsMutating: true),
        new("delete_event", "Delete one occurrence of an event.",
            """
            {"type":"object","properties":{
              "targetId":{"type":"string"},"reason":{"type":"string"}},
             "required":["targetId"]}
            """, IsMutating: true, IsCritical: true),
        new("copy_event", "Copy an event to another date, keeping its time of day.",
            """
            {"type":"object","properties":{
              "targetId":{"type":"string"},
              "targetDate":{"type":"string","description":"YYYY-MM-DD"},
              "reason":{"type":"string"}},
             "required":["targetId","targetDate"]}
            """, IsMutating: true),
        new("create_task",
            "Capture an intention without booking time for it. It stays in the unscheduled list and does not occupy the calendar.",
            """
            {"type":"object","properties":{
              "title":{"type":"string"},
              "durationMinutes":{"type":"integer","description":"Rough estimate. Default 30."},
              "priority":{"type":"integer","description":"0-100. Default 50."},
              "deadlineLocal":{"type":"string","description":"Local ISO-8601 deadline."},
              "preferredTime":{"type":"string","enum":["Any","Morning","Afternoon","Evening"]},
              "reason":{"type":"string"}},
             "required":["title"]}
            """, IsMutating: true),
        new("create_events",
            "Book several slots at once. Use this for anything covering more than one event — a planned day, a week, a repeated block — instead of calling create_event repeatedly. Up to 40 entries.",
            """
            {"type":"object","properties":{
              "events":{"type":"array","description":"Each entry is one event.","items":{
                "type":"object","properties":{
                  "title":{"type":"string"},
                  "startLocal":{"type":"string","description":"Local ISO-8601, for example 2026-08-24T15:30:00."},
                  "endLocal":{"type":"string","description":"Local ISO-8601. Omit only if durationMinutes is given."},
                  "durationMinutes":{"type":"integer"},
                  "category":{"type":"string","enum":["General","Work","Personal","Study","Health","Social","Errands","Travel","Rest"]},
                  "priority":{"type":"integer","description":"0-100. 65+ is high, 90+ is critical."},
                  "reason":{"type":"string"}},
                "required":["title","startLocal"]}}},
             "required":["events"]}
            """, IsMutating: true),
        new("update_task", "Change an existing task. Use an exact id from get_tasks.",
            """
            {"type":"object","properties":{
              "targetId":{"type":"string"},
              "title":{"type":"string"},
              "durationMinutes":{"type":"integer"},
              "priority":{"type":"integer"},
              "deadlineLocal":{"type":"string"},
              "preferredTime":{"type":"string","enum":["Any","Morning","Afternoon","Evening"]},
              "reason":{"type":"string"}},
             "required":["targetId"]}
            """, IsMutating: true),
        new("delete_task", "Delete a task.",
            """{"type":"object","properties":{"targetId":{"type":"string"},"reason":{"type":"string"}},"required":["targetId"]}""",
            IsMutating: true, IsCritical: true),
        new("set_task_status",
            "Mark a task started, finished, dropped or back on the list. This is how a task is completed — deleting it instead destroys the history the statistics are built from.",
            """
            {"type":"object","properties":{
              "targetId":{"type":"string","description":"Task id."},
              "status":{"type":"string","enum":["Inbox","Ready","Scheduled","InProgress","Done","Cancelled"]},
              "reason":{"type":"string"}},
             "required":["targetId","status"]}
            """, IsMutating: true),
        new("create_focus_block",
            "Set working time aside for a task that already exists, without turning it into an appointment. Use this when the user wants time to DO something on their list; use create_event for anything that is an appointment in its own right.",
            """
            {"type":"object","properties":{
              "targetId":{"type":"string","description":"Task id from get_tasks or search_schedule."},
              "startLocal":{"type":"string","description":"Local ISO-8601."},
              "endLocal":{"type":"string","description":"Local ISO-8601. Omit to use durationMinutes, or the task's remaining time."},
              "durationMinutes":{"type":"integer"},
              "reason":{"type":"string"}},
             "required":["targetId","startLocal"]}
            """, IsMutating: true),
        new("shift_day", "Move every event on one day earlier or later by the same amount.",
            """
            {"type":"object","properties":{
              "sourceDate":{"type":"string","description":"YYYY-MM-DD"},
              "offsetMinutes":{"type":"integer","description":"Negative moves earlier."},
              "reason":{"type":"string"}},
             "required":["sourceDate","offsetMinutes"]}
            """, IsMutating: true, IsCritical: true),
        new("copy_day", "Copy every event from one day to another.",
            """
            {"type":"object","properties":{
              "sourceDate":{"type":"string","description":"YYYY-MM-DD"},
              "targetDate":{"type":"string","description":"YYYY-MM-DD"},
              "reason":{"type":"string"}},
             "required":["sourceDate","targetDate"]}
            """, IsMutating: true, IsCritical: true)
    ];

    private static string Local(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    private static DateTimeOffset LocalMoment(DateOnly date, int hour)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddHours(hour), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset RoundUp(DateTimeOffset value, int minutes)
    {
        var ticks = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTimeOffset((value.Ticks + ticks - 1) / ticks * ticks, value.Offset);
    }
}
