using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;
using System.Text.Json;

namespace IntelligentScheduler.Infrastructure;

public sealed class SqliteStore(IDbContextFactory<AppDbContext> factory) :
    ITaskRepository, ICalendarRepository, IBlockRepository, IAvailabilityRepository, ISettingsRepository,
    ISchedulerDiagnosticsRepository, ITaskExecutionRepository
{
    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks.AsNoTracking().OrderBy(t => t.Status).ThenByDescending(t => t.Priority).ThenBy(t => t.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskItem>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks.AsNoTracking()
            .Where(t => t.Status != DomainTaskStatus.Done && t.Status != DomainTaskStatus.Cancelled && t.RemainingDurationMin > 0)
            .OrderByDescending(t => t.Priority).ThenBy(t => t.DeadlineUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        task.Validate();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        task.Validate();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Tasks.FindAsync([task.Id], cancellationToken) ?? throw new InvalidOperationException("Task no longer exists.");
        existing.Title = task.Title.Trim();
        existing.Notes = task.Notes;
        existing.Status = task.Status;
        existing.Priority = task.Priority;
        existing.EstimatedDurationMin = task.EstimatedDurationMin;
        existing.RemainingDurationMin = task.RemainingDurationMin;
        existing.DeadlineUtc = task.DeadlineUtc;
        existing.EarliestStartUtc = task.EarliestStartUtc;
        existing.Splittable = task.Splittable;
        existing.MinBlockMin = task.MinBlockMin;
        existing.MaxSplits = task.MaxSplits;
        existing.EnergyRequired = task.EnergyRequired;
        existing.PreferredTimeOfDay = task.PreferredTimeOfDay;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        existing.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hasTimeBlocks = await TableExistsAsync(db, "TimeBlocks", cancellationToken);
        var hasTaskScheduleExclusions = await TableExistsAsync(db, "TaskScheduleExclusions", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var task = await db.Tasks.FindAsync([id], cancellationToken);
        if (task is null) return;

        // These are legacy scheduling tables. Some databases created by older builds do not
        // contain them, and an unscheduled task must still remain removable.
        if (hasTimeBlocks)
        {
            await db.TimeBlocks
                .Where(block => block.TaskId == id && (block.State == BlockState.Planned || block.State == BlockState.InProgress))
                .ExecuteDeleteAsync(cancellationToken);

            var history = await db.TimeBlocks.Where(block => block.TaskId == id).ToListAsync(cancellationToken);
            foreach (var block in history)
            {
                block.TitleOverride ??= task.Title;
                block.TaskId = null;
                block.Task = null;
            }
        }

        if (hasTaskScheduleExclusions)
            await db.TaskScheduleExclusions.Where(exclusion => exclusion.TaskId == id).ExecuteDeleteAsync(cancellationToken);

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        AppDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
    async Task<IReadOnlyList<CalendarEvent>> ICalendarRepository.GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CalendarEvents.AsNoTracking().OrderBy(e => e.StartUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetRangeAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CalendarEvents.AsNoTracking().Where(e => e.StartUtc < endUtc && e.EndUtc > startUtc).OrderBy(e => e.StartUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];
        var wanted = ids.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CalendarEvents.AsNoTracking().Where(e => wanted.Contains(e.Id)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> GetLiveSeriesAsync(IReadOnlyCollection<Guid> seriesIds, CancellationToken cancellationToken = default)
    {
        if (seriesIds.Count == 0) return new HashSet<Guid>();
        var wanted = seriesIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var live = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.SeriesId != null && wanted.Contains(e.SeriesId.Value))
            .Select(e => e.SeriesId!.Value).Distinct().ToListAsync(cancellationToken);
        return live.ToHashSet();
    }

    public async Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ValidateCalendarEvent(calendarEvent);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CalendarEvents.Add(calendarEvent);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IReadOnlyCollection<CalendarEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;
        foreach (var calendarEvent in events) ValidateCalendarEvent(calendarEvent);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CalendarEvents.AddRange(events);
        await db.SaveChangesAsync(cancellationToken);
    }
    public async Task<int> ShiftRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        if (endUtc <= startUtc) throw new ArgumentException("The range end must be after its start.");
        if (offset == TimeSpan.Zero) return 0;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var events = await db.CalendarEvents
            .Where(item => item.StartUtc < endUtc && item.EndUtc > startUtc)
            .ToListAsync(cancellationToken);
        foreach (var calendarEvent in events)
        {
            calendarEvent.StartUtc += offset;
            calendarEvent.EndUtc += offset;
            calendarEvent.IsRecurrenceException = calendarEvent.SeriesId.HasValue;
            calendarEvent.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return events.Count;
    }

    public async Task<int> MoveEventsAsync(IReadOnlyCollection<EventMove> moves, CancellationToken cancellationToken = default)
    {
        if (moves.Count == 0) return 0;
        foreach (var move in moves)
            if (move.ToEndUtc <= move.ToStartUtc)
                throw new ArgumentException("A moved event must keep a positive duration.", nameof(moves));

        var byId = moves.GroupBy(move => move.Id).ToDictionary(group => group.Key, group => group.Last());
        var ids = byId.Keys.ToList();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var targets = await db.CalendarEvents.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        foreach (var target in targets)
        {
            var move = byId[target.Id];
            target.StartUtc = move.ToStartUtc;
            target.EndUtc = move.ToEndUtc;
            // A moved occurrence no longer follows its series, so later edits to the series
            // must leave it where the user put it.
            if (target.SeriesId.HasValue) target.IsRecurrenceException = true;
            target.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return targets.Count;
    }

    public async Task<int> DeleteManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;
        var distinct = ids.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CalendarEvents.Where(item => distinct.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task UpdateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ValidateCalendarEvent(calendarEvent);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CalendarEvents.FindAsync([calendarEvent.Id], cancellationToken) ?? throw new InvalidOperationException("Event no longer exists.");
        existing.Title = calendarEvent.Title.Trim();
        existing.StartUtc = calendarEvent.StartUtc;
        existing.EndUtc = calendarEvent.EndUtc;
        existing.Source = calendarEvent.Source;
        existing.ExternalId = calendarEvent.ExternalId;
        existing.Category = calendarEvent.Category;
        existing.Priority = calendarEvent.Priority;
        existing.Flexibility = calendarEvent.Flexibility;
        existing.Recurrence = calendarEvent.Recurrence;
        existing.SeriesId = calendarEvent.SeriesId;
        existing.OccurrenceIndex = calendarEvent.OccurrenceIndex;
        existing.IsRecurrenceException = calendarEvent.IsRecurrenceException;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAsync(TimeBlock block, CancellationToken cancellationToken = default)
    {
        if (block.EndUtc <= block.StartUtc) throw new ArgumentException("A block must have a positive duration.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        block.Task = null;
        db.TimeBlocks.Add(block);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateScopeAsync(CalendarEvent original, CalendarEvent updated, EventSeriesScope scope, CancellationToken cancellationToken = default)
    {
        ValidateCalendarEvent(updated);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var targets = scope == EventSeriesScope.Occurrence || original.SeriesId is null
            ? await db.CalendarEvents.Where(item => item.Id == original.Id).ToListAsync(cancellationToken)
            : scope == EventSeriesScope.ThisAndFollowing
                ? await db.CalendarEvents.Where(item => item.SeriesId == original.SeriesId && item.StartUtc >= original.StartUtc).ToListAsync(cancellationToken)
                : await db.CalendarEvents.Where(item => item.SeriesId == original.SeriesId).ToListAsync(cancellationToken);
        var startDelta = updated.StartUtc - original.StartUtc;
        var endDelta = updated.EndUtc - original.EndUtc;
        foreach (var target in targets)
        {
            target.Title = updated.Title.Trim();
            target.Category = updated.Category;
            target.Priority = updated.Priority;
            target.Flexibility = updated.Flexibility;
            target.Recurrence = updated.Recurrence;
            target.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (target.Id == original.Id)
            {
                target.StartUtc = updated.StartUtc;
                target.EndUtc = updated.EndUtc;
            }
            else
            {
                target.StartUtc += startDelta;
                target.EndUtc += endDelta;
            }
            target.IsRecurrenceException = scope == EventSeriesScope.Occurrence;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceScopeAsync(
        CalendarEvent original,
        EventSeriesScope scope,
        IReadOnlyCollection<CalendarEvent> replacements,
        CancellationToken cancellationToken = default)
    {
        foreach (var replacement in replacements) ValidateCalendarEvent(replacement);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var query = scope == EventSeriesScope.Occurrence || original.SeriesId is null
            ? db.CalendarEvents.Where(item => item.Id == original.Id)
            : scope == EventSeriesScope.ThisAndFollowing
                ? db.CalendarEvents.Where(item => item.SeriesId == original.SeriesId && item.StartUtc >= original.StartUtc)
                : db.CalendarEvents.Where(item => item.SeriesId == original.SeriesId);
        await query.ExecuteDeleteAsync(cancellationToken);
        db.CalendarEvents.AddRange(replacements);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task DeleteScopeAsync(CalendarEvent calendarEvent, EventSeriesScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = scope == EventSeriesScope.Occurrence || calendarEvent.SeriesId is null
            ? db.CalendarEvents.Where(item => item.Id == calendarEvent.Id)
            : scope == EventSeriesScope.ThisAndFollowing
                ? db.CalendarEvents.Where(item => item.SeriesId == calendarEvent.SeriesId && item.StartUtc >= calendarEvent.StartUtc)
                : db.CalendarEvents.Where(item => item.SeriesId == calendarEvent.SeriesId);
        await query.ExecuteDeleteAsync(cancellationToken);
    }
    private static void ValidateCalendarEvent(CalendarEvent calendarEvent)
    {
        if (string.IsNullOrWhiteSpace(calendarEvent.Title) || calendarEvent.EndUtc <= calendarEvent.StartUtc)
            throw new ArgumentException("A calendar event needs a title and a valid time range.");
    }
    private async Task DeleteCalendarEventAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.CalendarEvents.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    Task ICalendarRepository.DeleteAsync(Guid id, CancellationToken cancellationToken) => DeleteCalendarEventAsync(id, cancellationToken);

    async Task<IReadOnlyList<TimeBlock>> IBlockRepository.GetRangeAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TimeBlocks.AsNoTracking().Include(b => b.Task)
            .Where(b => b.StartUtc < endUtc && b.EndUtc > startUtc && b.State != BlockState.Cancelled && b.State != BlockState.Skipped)
            .OrderBy(b => b.StartUtc).ToListAsync(cancellationToken);
    }

    async Task<IReadOnlyList<TimeBlock>> IBlockRepository.GetHistoryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TimeBlocks.AsNoTracking().Include(b => b.Task)
            .Where(b => b.StartUtc < endUtc && b.EndUtc > startUtc && b.State != BlockState.Cancelled)
            .OrderBy(b => b.StartUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskScheduleExclusion>> GetExclusionsAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TaskScheduleExclusions.AsNoTracking()
            .Where(exclusion => exclusion.StartUtc < endUtc && exclusion.EndUtc > startUtc)
            .OrderBy(exclusion => exclusion.StartUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AddExclusionAsync(TaskScheduleExclusion exclusion, CancellationToken cancellationToken = default)
    {
        if (exclusion.EndUtc <= exclusion.StartUtc)
            throw new ArgumentException("A task scheduling exclusion needs a valid time range.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.TaskScheduleExclusions.Add(exclusion);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(TimeBlock block, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.TimeBlocks.FindAsync([block.Id], cancellationToken) ?? throw new InvalidOperationException("Block no longer exists.");
        existing.StartUtc = block.StartUtc;
        existing.EndUtc = block.EndUtc;
        existing.State = block.State;
        existing.Origin = block.Origin;
        existing.IsLocked = block.IsLocked;
        existing.PreviousStartUtc = block.PreviousStartUtc;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TaskBlockCompletionResult> CompleteTaskBlockAsync(
        Guid blockId, int actualMinutes, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var block = await db.TimeBlocks.Include(item => item.Task)
            .SingleOrDefaultAsync(item => item.Id == blockId, cancellationToken)
            ?? throw new InvalidOperationException("Task block no longer exists.");
        var plannedMinutes = block.DurationMinutes;
        if (block.State is not (BlockState.Planned or BlockState.InProgress) || actualMinutes <= 0 || actualMinutes > plannedMinutes)
            throw new ArgumentOutOfRangeException(nameof(actualMinutes));

        if (actualMinutes < plannedMinutes) block.EndUtc = block.StartUtc.AddMinutes(actualMinutes);
        block.State = BlockState.Completed;
        block.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var remaining = 0;
        var taskCompleted = false;
        if (block.Task is { } task)
        {
            task.RemainingDurationMin = Math.Max(0, task.RemainingDurationMin - actualMinutes);
            task.Status = task.RemainingDurationMin == 0 ? DomainTaskStatus.Done : DomainTaskStatus.Ready;
            task.UpdatedAtUtc = DateTimeOffset.UtcNow;
            task.Version++;
            remaining = task.RemainingDurationMin;
            taskCompleted = task.Status == DomainTaskStatus.Done;
            db.TaskExecutions.Add(new TaskExecution
            {
                TaskId = task.Id,
                TimeBlockId = block.Id,
                PlannedMinutes = plannedMinutes,
                ActualMinutes = actualMinutes
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskBlockCompletionResult(plannedMinutes, actualMinutes, remaining, taskCompleted);
    }

    public async Task ApplyPlanAsync(ScheduleResult result, DateTimeOffset horizonStartUtc, DateTimeOffset horizonEndUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.TimeBlocks.Where(b => b.StartUtc < horizonEndUtc && b.EndUtc > horizonStartUtc &&
                                      b.Origin == BlockOrigin.Solver && b.State == BlockState.Planned && !b.IsLocked)
            .ExecuteDeleteAsync(cancellationToken);

        db.PlanRevisions.Add(new PlanRevision
        {
            Id = result.RevisionId,
            HorizonStartUtc = horizonStartUtc,
            HorizonEndUtc = horizonEndUtc,
            Status = result.Status,
            ObjectiveScore = result.ObjectiveScore,
            SolveDurationMs = result.SolveDurationMs
        });
        foreach (var block in result.PlannedBlocks)
        {
            block.Task = null;
            db.TimeBlocks.Add(block);
        }

        var scheduledTaskIds = result.PlannedBlocks.Where(b => b.TaskId.HasValue).Select(b => b.TaskId!.Value).Distinct().ToArray();
        var scheduledTasks = await db.Tasks.Where(t => scheduledTaskIds.Contains(t.Id)).ToListAsync(cancellationToken);
        foreach (var task in scheduledTasks)
            if (task.Status is DomainTaskStatus.Inbox or DomainTaskStatus.Ready) task.Status = DomainTaskStatus.Scheduled;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilityWindow>> GetWindowsAsync(DateTimeOffset horizonStartUtc, DateTimeOffset horizonEndUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rules = await db.AvailabilityRules.AsNoTracking().Where(r => r.IsEnabled).ToListAsync(cancellationToken);
        var exceptions = await db.AvailabilityExceptions.AsNoTracking().ToListAsync(cancellationToken);
        var windows = new List<AvailabilityWindow>();
        var localStart = horizonStartUtc.ToLocalTime();
        var localEnd = horizonEndUtc.ToLocalTime();

        for (var date = DateOnly.FromDateTime(localStart.Date); date <= DateOnly.FromDateTime(localEnd.Date); date = date.AddDays(1))
        {
            var dayRules = rules.Where(r => r.DayOfWeek == date.DayOfWeek).ToList();
            foreach (var rule in dayRules)
            {
                var unavailable = exceptions.Where(e => e.Date == date && e.Kind == AvailabilityExceptionKind.Unavailable).ToList();
                var baseWindow = ToWindow(date, rule.StartLocal, rule.EndLocal);
                var fragments = new List<AvailabilityWindow> { baseWindow };
                foreach (var cut in unavailable.Select(e => ToWindow(date, e.StartLocal, e.EndLocal)))
                    fragments = fragments.SelectMany(w => Subtract(w, cut)).ToList();
                windows.AddRange(fragments);
            }
            windows.AddRange(exceptions.Where(e => e.Date == date && e.Kind == AvailabilityExceptionKind.Available)
                .Select(e => ToWindow(date, e.StartLocal, e.EndLocal)));
        }

        return windows.Where(w => w.EndUtc > horizonStartUtc && w.StartUtc < horizonEndUtc)
            .Select(w => new AvailabilityWindow(Max(w.StartUtc, horizonStartUtc), Min(w.EndUtc, horizonEndUtc)))
            .OrderBy(w => w.StartUtc).ToList();
    }

    public async Task<IReadOnlyList<AvailabilityRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AvailabilityRules.AsNoTracking().OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartLocal).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilityException>> GetExceptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AvailabilityExceptions.AsNoTracking().OrderBy(e => e.Date).ThenBy(e => e.StartLocal).ToListAsync(cancellationToken);
    }

    public async Task UpdateRuleAsync(AvailabilityRule rule, CancellationToken cancellationToken = default)
    {
        if (rule.EndLocal <= rule.StartLocal) throw new ArgumentException("Availability must end after it starts.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AvailabilityRules.FindAsync([rule.Id], cancellationToken) ?? throw new InvalidOperationException("Availability rule no longer exists.");
        existing.StartLocal = rule.StartLocal;
        existing.EndLocal = rule.EndLocal;
        existing.IsEnabled = rule.IsEnabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddExceptionAsync(AvailabilityException exception, CancellationToken cancellationToken = default)
    {
        if (exception.EndLocal <= exception.StartLocal) throw new ArgumentException("Exception must end after it starts.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AvailabilityExceptions.Add(exception);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteExceptionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.AvailabilityExceptions.Where(e => e.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<PlanningProfile> GetPlanningProfileAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.PlanningProfiles.AsNoTracking().FirstAsync(cancellationToken);
    }

    public async Task UpdatePlanningProfileAsync(PlanningProfile profile, CancellationToken cancellationToken = default)
    {
        profile.SchedulingUnitMinutes = profile.SchedulingUnitMinutes is 5 or 10 ? profile.SchedulingUnitMinutes : 5;
        profile.MaxSolveTimeMs = Math.Clamp(profile.MaxSolveTimeMs, 250, 10_000);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PlanningProfiles.FindAsync([profile.Id], cancellationToken) ?? throw new InvalidOperationException("Planning profile no longer exists.");
        existing.TimeZoneId = profile.TimeZoneId;
        existing.SchedulingUnitMinutes = profile.SchedulingUnitMinutes;
        existing.BufferBeforeFixedMin = Math.Clamp(profile.BufferBeforeFixedMin, 0, 120);
        existing.BufferAfterFixedMin = Math.Clamp(profile.BufferAfterFixedMin, 0, 120);
        existing.MaxSolveTimeMs = profile.MaxSolveTimeMs;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings.AsNoTracking().FirstAsync(cancellationToken);
    }

    public async Task UpdateAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AppSettings.FindAsync([settings.Id], cancellationToken) ?? throw new InvalidOperationException("App settings no longer exist.");
        existing.NotificationsEnabled = settings.NotificationsEnabled;
        existing.NotificationSoundEnabled = settings.NotificationSoundEnabled;
        existing.DoNotDisturbUntilUtc = settings.DoNotDisturbUntilUtc;
        existing.DoNotDisturbUntilResumed = settings.DoNotDisturbUntilResumed;
        existing.DoNotDisturbMinutes = settings.DoNotDisturbMinutes;
        existing.FocusModeEnabled = settings.FocusModeEnabled;
        existing.FocusModeReleasesShortcuts = settings.FocusModeReleasesShortcuts;
        existing.FocusModeHoldsReminders = settings.FocusModeHoldsReminders;
        existing.FocusModeHidesHandle = settings.FocusModeHidesHandle;
        existing.LocalModelGpuEnabled = settings.LocalModelGpuEnabled;
        existing.AutostartEnabled = settings.AutostartEnabled;
        existing.MinimizeToTray = settings.MinimizeToTray;
        existing.QuickAddHotkey = string.IsNullOrWhiteSpace(settings.QuickAddHotkey) ? "Ctrl+Alt+Space" : settings.QuickAddHotkey.Trim();
        // Read back through the same reader the shell uses, so a hand-edited or truncated map
        // is repaired here rather than stored and puzzled over later.
        existing.ShortcutsJson = string.IsNullOrWhiteSpace(settings.ShortcutsJson)
            ? string.Empty
            : ShellShortcuts.Write(ShellShortcuts.Read(settings.ShortcutsJson, existing.QuickAddHotkey));
        existing.AiProviderMode = settings.AiProviderMode;
        existing.BackupRetentionCount = Math.Clamp(settings.BackupRetentionCount, 1, 30);
        existing.AssistantProvider = settings.AssistantProvider;
        existing.AssistantAccessLevel = settings.AssistantAccessLevel;
        existing.ShareContextWithExternalAi = settings.ShareContextWithExternalAi;
        existing.AssistantEndpoint = settings.AssistantEndpoint.Trim();
        existing.AssistantModel = settings.AssistantModel.Trim();
        existing.AssistantApiKeyCiphertext = settings.AssistantApiKeyCiphertext;
        existing.McpTransport = settings.McpTransport;
        existing.McpEndpoint = (settings.McpEndpoint ?? string.Empty).Trim();
        existing.McpCommand = settings.McpCommand.Trim();
        existing.McpArguments = settings.McpArguments.Trim();
        existing.McpToolName = settings.McpToolName.Trim();
        existing.McpToolsEnabled = settings.McpToolsEnabled;
        existing.AssistantMemoryEnabled = settings.AssistantMemoryEnabled;
        existing.ActiveConversationId = settings.ActiveConversationId;
        // A user-supplied file keeps its reserved id; anything else is normalised to the catalogue.
        existing.LocalModelId = LocalModelCatalog.IsCustom(settings.LocalModelId)
            ? LocalModelCatalog.CustomModelId
            : LocalModelCatalog.Find(settings.LocalModelId).Id;
        existing.LocalModelVisionEnabled = settings.LocalModelVisionEnabled;
        existing.LocalModelIdleMinutes = Math.Max(0, settings.LocalModelIdleMinutes);
        existing.LocalModelAutoTune = settings.LocalModelAutoTune;
        existing.LocalModelContextTokens = Math.Clamp(
            settings.LocalModelContextTokens <= 0 ? 8192 : settings.LocalModelContextTokens, 2048, 131_072);
        existing.LocalModelThreads = Math.Clamp(settings.LocalModelThreads, 0, 256);
        existing.CustomModelPath = settings.CustomModelPath?.Trim() ?? string.Empty;
        existing.CustomProjectorPath = settings.CustomProjectorPath?.Trim() ?? string.Empty;
        existing.AssistantTemperature = Math.Clamp(settings.AssistantTemperature, 0, 2);
        existing.AssistantMaxTokens = Math.Clamp(
            settings.AssistantMaxTokens <= 0 ? 2000 : settings.AssistantMaxTokens, 64, 32_000);
        existing.AssistantTopP = Math.Clamp(settings.AssistantTopP <= 0 ? 1 : settings.AssistantTopP, 0.05, 1);
        existing.AssistantMaxToolIterations = Math.Clamp(
            settings.AssistantMaxToolIterations <= 0 ? 8 : settings.AssistantMaxToolIterations, 1, 16);
        existing.AssistantHistoryTurns = Math.Clamp(
            settings.AssistantHistoryTurns <= 0 ? 24 : settings.AssistantHistoryTurns, 2, 64);
        existing.AssistantTimeoutMinutes = Math.Clamp(
            settings.AssistantTimeoutMinutes <= 0 ? 5 : settings.AssistantTimeoutMinutes, 1, 30);
        existing.LocalTemperature = Math.Clamp(settings.LocalTemperature, 0, 2);
        existing.LocalTopP = Math.Clamp(settings.LocalTopP <= 0 ? 1 : settings.LocalTopP, 0.05, 1);
        existing.LocalMaxTokens = Math.Clamp(settings.LocalMaxTokens <= 0 ? 3000 : settings.LocalMaxTokens, 64, 32_000);
        existing.LocalMaxToolIterations = Math.Clamp(
            settings.LocalMaxToolIterations <= 0 ? 8 : settings.LocalMaxToolIterations, 1, 16);
        existing.LocalHistoryTurns = Math.Clamp(settings.LocalHistoryTurns <= 0 ? 16 : settings.LocalHistoryTurns, 2, 64);
        existing.LocalTimeoutMinutes = Math.Clamp(
            settings.LocalTimeoutMinutes <= 0 ? 10 : settings.LocalTimeoutMinutes, 1, 30);
        existing.AssistantExtraInstructions = settings.AssistantExtraInstructions?.Trim() ?? string.Empty;
        MarelianAppearance.NormalizeSettings(settings);
        existing.ShellTheme = ShellTheme.Marelian;
        existing.ShellThemeMode = settings.ShellThemeMode;
        existing.ThemeColorSource = settings.ThemeColorSource;
        existing.VisualPalette = settings.VisualPalette;
        existing.AppFontFamily = settings.AppFontFamily;
        existing.CustomAccent = settings.CustomAccent;
        existing.CustomBackground = settings.CustomBackground;
        existing.ThemeColorOverridesJson = settings.ThemeColorOverridesJson;
        existing.MascotColor = settings.MascotColor?.Trim() ?? string.Empty;
        existing.ShowMascot = settings.ShowMascot;
        existing.EnableTransparency = settings.EnableTransparency;
        existing.TransparencyIntensity = Math.Clamp(settings.TransparencyIntensity, 0, 80);
        existing.RevealMode = settings.RevealMode;
        existing.WidgetHideDelayMs = Math.Clamp(
            settings.WidgetHideDelayMs <= 0 ? 1500 : settings.WidgetHideDelayMs, 400, 6000);
        existing.WidgetKeepOpen = settings.WidgetKeepOpen;
        existing.PinOpen = settings.PinOpen;
        existing.AutoPlan = settings.AutoPlan;
        existing.LanguageCode = settings.LanguageCode;
        existing.ReduceMotion = settings.ReduceMotion;
        existing.StartHidden = settings.StartHidden;
        existing.KeepOnTop = settings.KeepOnTop;
        existing.Use24HourClock = settings.Use24HourClock;
        existing.LastSection = settings.LastSection;

        existing.UpdateCheckEnabled = settings.UpdateCheckEnabled;

        existing.CloudSyncProvider = settings.CloudSyncProvider;
        existing.CloudSyncFolderPath = settings.CloudSyncFolderPath?.Trim() ?? string.Empty;
        existing.CloudSyncAutomatic = settings.CloudSyncAutomatic;
        existing.CloudSyncDeviceName = settings.CloudSyncDeviceName?.Trim() ?? string.Empty;
        existing.GoogleDriveClientId = settings.GoogleDriveClientId?.Trim() ?? string.Empty;
        existing.GoogleDriveClientSecretCiphertext = settings.GoogleDriveClientSecretCiphertext;

        // Deliberately not copied: the Google sign-in, the sync revision, stamp and fingerprint,
        // and the release check's stamps. The shell saves its whole settings object on every
        // preference change, and it holds whatever those fields said when it loaded them. Copying
        // them here is how a Google sign-in written a moment earlier was erased by the next
        // unrelated save — and the sync that followed asked the user to connect the account they
        // had just connected. Their owners write them through PatchAppSettingsAsync.

        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PatchAppSettingsAsync(Action<AppSettings> change, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AppSettings.FirstAsync(cancellationToken);
        change(existing);
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(PlanningRequest request, ScheduleResult result, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.SchedulerDiagnostics.Add(new SchedulerDiagnostic
        {
            RevisionId = result.RevisionId,
            HorizonStartUtc = request.HorizonStartUtc,
            HorizonEndUtc = request.HorizonEndUtc,
            TaskCount = request.Tasks.Count,
            FixedEventCount = request.FixedEvents.Count,
            ExistingBlockCount = request.ExistingBlocks.Count,
            AvailabilityWindowCount = request.AvailabilityWindows.Count,
            Status = result.Status.ToString(),
            ObjectiveScore = result.ObjectiveScore,
            SolveDurationMs = result.SolveDurationMs,
            ResultSummaryJson = JsonSerializer.Serialize(new
            {
                plannedBlocks = result.PlannedBlocks.Count,
                unscheduled = result.UnscheduledTasks.Select(x => new { x.TaskId, x.MissingMinutes, reason = x.ReasonCode.ToString() }),
                result.ExplanationHints
            })
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAsync(TaskExecution execution, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.TaskExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskExecution>> GetRecentAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TaskExecutions.AsNoTracking()
            .Where(execution => execution.CompletedAtUtc >= sinceUtc)
            .OrderByDescending(execution => execution.CompletedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    private static AvailabilityWindow ToWindow(DateOnly date, TimeOnly start, TimeOnly end)
    {
        var startLocal = date.ToDateTime(start, DateTimeKind.Unspecified);
        var endLocal = date.ToDateTime(end, DateTimeKind.Unspecified);
        var startOffset = TimeZoneInfo.Local.GetUtcOffset(startLocal);
        var endOffset = TimeZoneInfo.Local.GetUtcOffset(endLocal);
        return new AvailabilityWindow(new DateTimeOffset(startLocal, startOffset).ToUniversalTime(), new DateTimeOffset(endLocal, endOffset).ToUniversalTime());
    }

    private static IEnumerable<AvailabilityWindow> Subtract(AvailabilityWindow source, AvailabilityWindow cut)
    {
        if (cut.EndUtc <= source.StartUtc || cut.StartUtc >= source.EndUtc) return [source];
        var result = new List<AvailabilityWindow>();
        if (cut.StartUtc > source.StartUtc) result.Add(new AvailabilityWindow(source.StartUtc, cut.StartUtc));
        if (cut.EndUtc < source.EndUtc) result.Add(new AvailabilityWindow(cut.EndUtc, source.EndUtc));
        return result;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
