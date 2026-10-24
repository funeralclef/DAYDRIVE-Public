using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// Projects, their steps and the ticks a scheduled project's steps are given per sitting. The
/// first two tables are read whole: a person keeps a handful of projects with a few dozen steps
/// between them, and every screen that shows them needs all of them. Ticks pile up with every
/// occurrence of a repeating checklist, so those are read for the sittings on screen only.
/// </summary>
public sealed class ProjectStore(IDbContextFactory<AppDbContext> factory) : IProjectRepository
{
    private const int TitleLimit = 300;

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Projects.AsNoTracking().OrderBy(project => project.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectStep>> GetStepsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ProjectSteps.AsNoTracking()
            .OrderBy(step => step.ProjectId).ThenBy(step => step.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task AddProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        ValidateHost(project);
        project.Title = project.Title?.Trim() ?? string.Empty;
        if (project.Title.Length > TitleLimit) project.Title = project.Title[..TitleLimit];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        ValidateHost(project);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Projects.FindAsync([project.Id], cancellationToken)
                       ?? throw new InvalidOperationException("Project no longer exists.");
        var title = project.Title?.Trim() ?? string.Empty;
        existing.Title = title.Length > TitleLimit ? title[..TitleLimit] : title;
        existing.Category = project.Category;
        existing.Deadline = project.Deadline;
        existing.TaskId = project.TaskId;
        existing.EventId = project.EventId;
        existing.SeriesId = project.SeriesId;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Said outright rather than left to the foreign key: SQLite honours a cascade only on a
        // connection that has switched foreign keys on, and a copy of the database opened by
        // anything else may not have. The ticks have no key to cascade along at all.
        var steps = db.ProjectSteps.Where(step => step.ProjectId == id).Select(step => step.Id);
        await db.ProjectStepChecks.Where(check => steps.Contains(check.StepId)).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectSteps.Where(step => step.ProjectId == id).ExecuteDeleteAsync(cancellationToken);
        await db.Projects.Where(project => project.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task AddStepAsync(ProjectStep step, CancellationToken cancellationToken = default)
        => AddStepsAsync([step], cancellationToken);

    public async Task AddStepsAsync(IReadOnlyCollection<ProjectStep> steps, CancellationToken cancellationToken = default)
    {
        if (steps.Count == 0) return;
        foreach (var step in steps)
        {
            step.Title = CleanTitle(step.Title, "Step");
            ValidateLength(step);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var owners = steps.Select(step => step.ProjectId).Distinct().ToList();
        var known = await db.Projects.CountAsync(project => owners.Contains(project.Id), cancellationToken);
        if (known != owners.Count) throw new InvalidOperationException("Project no longer exists.");
        db.ProjectSteps.AddRange(steps);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateStepAsync(ProjectStep step, CancellationToken cancellationToken = default)
    {
        step.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return SaveStepsAsync([step], cancellationToken);
    }

    public async Task SaveStepsAsync(IReadOnlyCollection<ProjectStep> steps, CancellationToken cancellationToken = default)
    {
        if (steps.Count == 0) return;
        foreach (var step in steps)
        {
            step.Title = CleanTitle(step.Title, "Step");
            ValidateLength(step);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = steps.Select(step => step.Id).ToList();
        var stored = await db.ProjectSteps.Where(step => ids.Contains(step.Id)).ToDictionaryAsync(step => step.Id, cancellationToken);
        foreach (var step in steps)
        {
            // Removed in the meantime: there is nothing left to bring up to date.
            if (!stored.TryGetValue(step.Id, out var row)) continue;
            row.Title = step.Title;
            row.DurationMinutes = step.DurationMinutes;
            row.PlannedDate = step.PlannedDate;
            row.SortOrder = step.SortOrder;
            row.IsDone = step.IsDone;
            row.CompletedAtUtc = step.CompletedAtUtc;
            row.EventId = step.EventId;
            row.UpdatedAtUtc = step.UpdatedAtUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteStepAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.ProjectStepChecks.Where(check => check.StepId == id).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectSteps.Where(step => step.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task MoveStepsAsync(IReadOnlyCollection<Guid> stepIds, Guid projectId, CancellationToken cancellationToken = default)
    {
        if (stepIds.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            throw new InvalidOperationException("Project no longer exists.");
        var wanted = stepIds.Distinct().ToList();
        var moving = await db.ProjectSteps.Where(step => wanted.Contains(step.Id)).ToListAsync(cancellationToken);
        var next = await db.ProjectSteps.Where(step => step.ProjectId == projectId)
            .Select(step => (int?)step.SortOrder).MaxAsync(cancellationToken) ?? -1;
        var now = DateTimeOffset.UtcNow;
        // Kept in the order they had, so a list carried over reads the way it was written.
        foreach (var step in moving.OrderBy(step => step.SortOrder).ThenBy(step => step.CreatedAtUtc))
        {
            step.ProjectId = projectId;
            step.SortOrder = ++next;
            step.PlannedDate = null;
            step.EventId = null;
            step.IsDone = false;
            step.CompletedAtUtc = null;
            step.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectStepCheck>> GetChecksAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0) return [];
        var wanted = eventIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ProjectStepChecks.AsNoTracking().Where(check => wanted.Contains(check.EventId)).ToListAsync(cancellationToken);
    }

    public async Task CheckAsync(Guid eventId, IReadOnlyCollection<Guid> stepIds, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default)
    {
        if (stepIds.Count == 0) return;
        var wanted = stepIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var already = await db.ProjectStepChecks.Where(check => check.EventId == eventId && wanted.Contains(check.StepId))
            .Select(check => check.StepId).ToListAsync(cancellationToken);
        foreach (var stepId in wanted.Except(already))
            db.ProjectStepChecks.Add(new ProjectStepCheck { StepId = stepId, EventId = eventId, CompletedAtUtc = completedAtUtc });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UncheckAsync(Guid eventId, Guid stepId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.ProjectStepChecks.Where(check => check.EventId == eventId && check.StepId == stepId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> UncheckBatchAsync(Guid eventId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ProjectStepChecks.Where(check => check.EventId == eventId && check.CompletedAtUtc == completedAtUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearChecksAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0) return;
        var wanted = eventIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.ProjectStepChecks.Where(check => wanted.Contains(check.EventId)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> AdoptHostlessAsync(TimeZoneInfo zone, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hostless = await db.Projects
            .Where(project => project.TaskId == null && project.EventId == null && project.SeriesId == null)
            .ToListAsync(cancellationToken);
        if (hostless.Count == 0) return 0;

        var ids = hostless.Select(project => project.Id).ToList();
        var steps = (await db.ProjectSteps.AsNoTracking().Where(step => ids.Contains(step.ProjectId)).ToListAsync(cancellationToken))
            .ToLookup(step => step.ProjectId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var project in hostless)
        {
            var task = ProjectHosts.AdoptingTask(project, [.. steps[project.Id]], zone);
            db.Tasks.Add(task);
            project.TaskId = task.Id;
            project.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return hostless.Count;
    }

    private static void ValidateHost(Project project)
    {
        var hosts = (project.TaskId is null ? 0 : 1) + (project.EventId is null ? 0 : 1) + (project.SeriesId is null ? 0 : 1);
        if (hosts != 1) throw new ArgumentException("A project belongs to exactly one task, event or series.");
    }

    private static string CleanTitle(string? title, string what)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException($"{what} title is required.");
        return trimmed.Length > TitleLimit ? trimmed[..TitleLimit] : trimmed;
    }

    private static void ValidateLength(ProjectStep step)
    {
        if (step.DurationMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(step), "A step has to take some time.");
    }
}
