using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// Stores one verdict per calendar event. The planned start and length are copied in rather
/// than joined, so the history stays meaningful after the event is edited or deleted.
/// </summary>
public sealed class EventOutcomeStore(IDbContextFactory<AppDbContext> factory) : IEventOutcomeRepository
{
    public async Task<IReadOnlyList<EventOutcome>> GetRangeAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventOutcomes.AsNoTracking()
            .Where(outcome => outcome.PlannedStartUtc >= startUtc && outcome.PlannedStartUtc < endUtc)
            .OrderBy(outcome => outcome.PlannedStartUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<EventOutcome?> GetForEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventOutcomes.AsNoTracking()
            .FirstOrDefaultAsync(outcome => outcome.EventId == eventId, cancellationToken);
    }

    public async Task<IReadOnlyList<EventOutcome>> GetForEventsAsync(
        IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0) return [];
        var ids = eventIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventOutcomes.AsNoTracking()
            .Where(outcome => ids.Contains(outcome.EventId))
            .ToListAsync(cancellationToken);
    }

    public async Task<EventOutcome> RecordAsync(EventOutcome outcome, CancellationToken cancellationToken = default)
    {
        if (outcome.EventId == Guid.Empty) throw new ArgumentException("An outcome needs an event.", nameof(outcome));
        outcome.ActualMinutes = Math.Clamp(outcome.ActualMinutes, 0, 10_080);
        outcome.PlannedMinutes = Math.Clamp(outcome.PlannedMinutes, 0, 10_080);
        outcome.PausedSeconds = Math.Clamp(outcome.PausedSeconds, 0, 604_800);
        outcome.ProgressPercent = Math.Clamp(outcome.ProgressPercent, 0, 100);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.EventOutcomes.FirstOrDefaultAsync(
            item => item.EventId == outcome.EventId, cancellationToken);

        if (existing is null)
        {
            db.EventOutcomes.Add(outcome);
            await db.SaveChangesAsync(cancellationToken);
            return outcome;
        }

        existing.Kind = outcome.Kind;
        existing.PlannedStartUtc = outcome.PlannedStartUtc;
        existing.PlannedMinutes = outcome.PlannedMinutes;
        // A later verdict must not erase the moment the user actually pressed start.
        existing.StartedAtUtc = outcome.StartedAtUtc ?? existing.StartedAtUtc;
        existing.FinishedAtUtc = outcome.FinishedAtUtc;
        existing.ActualMinutes = outcome.ActualMinutes;
        existing.Note = outcome.Note;
        // The session's own bookkeeping. Pauses are carried by the caller, which is the only
        // place that knows whether this write is "resumed" or "stopped while still paused".
        existing.PausedSeconds = outcome.PausedSeconds;
        existing.PausedAtUtc = outcome.PausedAtUtc;
        existing.ProgressPercent = outcome.ProgressPercent;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> ClearAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventOutcomes.Where(outcome => outcome.EventId == eventId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }
}
