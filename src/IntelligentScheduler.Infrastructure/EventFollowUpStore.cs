using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// One follow-up row per event: where to pick the work up, and how often it has been pushed
/// onto a later day. Every write is a read-modify-write of that single row, so the three
/// callers below can each touch their own part of it without erasing the others.
/// </summary>
public sealed class EventFollowUpStore(IDbContextFactory<AppDbContext> factory) : IEventFollowUpRepository
{
    public async Task<IReadOnlyList<EventFollowUp>> GetForEventsAsync(
        IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0) return [];
        var ids = eventIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventFollowUps.AsNoTracking()
            .Where(item => ids.Contains(item.EventId))
            .ToListAsync(cancellationToken);
    }

    public async Task<EventFollowUp?> GetForEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventFollowUps.AsNoTracking()
            .FirstOrDefaultAsync(item => item.EventId == eventId, cancellationToken);
    }

    public Task<EventFollowUp> SetNextStepAsync(Guid eventId, string? nextStep, CancellationToken cancellationToken = default)
        => MutateAsync(eventId, row =>
        {
            var trimmed = nextStep?.Trim();
            row.NextStep = string.IsNullOrEmpty(trimmed)
                ? null
                : trimmed.Length > 400 ? trimmed[..400] : trimmed;
        }, cancellationToken);

    public Task<EventFollowUp> RecordDeferralAsync(Guid eventId, CancellationToken cancellationToken = default)
        => MutateAsync(eventId, row =>
        {
            row.DeferCount++;
            row.LastDeferredAtUtc = DateTimeOffset.UtcNow;
        }, cancellationToken);

    public Task<EventFollowUp> SetBlockerAsync(Guid eventId, EventBlocker blocker, CancellationToken cancellationToken = default)
        => MutateAsync(eventId, row => row.Blocker = blocker, cancellationToken);

    public async Task<bool> ClearAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.EventFollowUps.Where(item => item.EventId == eventId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private async Task<EventFollowUp> MutateAsync(
        Guid eventId, Action<EventFollowUp> change, CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("A follow-up needs an event.", nameof(eventId));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.EventFollowUps.FirstOrDefaultAsync(item => item.EventId == eventId, cancellationToken);
        if (row is null)
        {
            row = new EventFollowUp { EventId = eventId };
            db.EventFollowUps.Add(row);
        }

        change(row);
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }
}
