using IntelligentScheduler.Application;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

public sealed class SqliteBackupService(
    IDbContextFactory<AppDbContext> factory,
    ISettingsRepository settings,
    string databasePath) : IBackupService
{
    private string BackupDirectory => Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups");

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupDirectory);
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);

        var destination = Path.Combine(BackupDirectory, $"intelligent-scheduler-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.db");
        File.Copy(databasePath, destination, overwrite: false);
        await ApplyRetentionAsync(cancellationToken);
        return destination;
    }

    public Task<IReadOnlyList<string>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(BackupDirectory)) return Task.FromResult<IReadOnlyList<string>>([]);
        IReadOnlyList<string> files = Directory.GetFiles(BackupDirectory, "*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        return Task.FromResult(files);
    }

    private async Task ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var keep = (await settings.GetAppSettingsAsync(cancellationToken)).BackupRetentionCount;
        var old = Directory.GetFiles(BackupDirectory, "*.db").OrderByDescending(File.GetLastWriteTimeUtc).Skip(keep);
        foreach (var file in old) File.Delete(file);
    }
}
