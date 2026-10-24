using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

public sealed class DatabaseInitializer(IDbContextFactory<AppDbContext> factory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        if (!await db.AppSettings.AnyAsync(cancellationToken))
            db.AppSettings.Add(new AppSettings());
        else
            foreach (var settings in await db.AppSettings.ToListAsync(cancellationToken))
                MarelianAppearance.NormalizeSettings(settings);

        await db.SaveChangesAsync(cancellationToken);
    }
}
