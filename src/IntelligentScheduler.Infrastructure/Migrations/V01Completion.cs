using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608170002_V01Completion")]
public sealed class V01Completion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS TaskExecutions (
                Id TEXT NOT NULL PRIMARY KEY, TaskId TEXT NOT NULL, TimeBlockId TEXT NOT NULL,
                PlannedMinutes INTEGER NOT NULL, ActualMinutes INTEGER NOT NULL, CompletedAtUtc INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TaskExecutions_TaskId_CompletedAtUtc ON TaskExecutions(TaskId, CompletedAtUtc);

            CREATE TABLE IF NOT EXISTS SchedulerDiagnostics (
                Id TEXT NOT NULL PRIMARY KEY, RevisionId TEXT NOT NULL, HorizonStartUtc INTEGER NOT NULL,
                HorizonEndUtc INTEGER NOT NULL, TaskCount INTEGER NOT NULL, FixedEventCount INTEGER NOT NULL,
                ExistingBlockCount INTEGER NOT NULL, AvailabilityWindowCount INTEGER NOT NULL, Status TEXT NOT NULL,
                ObjectiveScore INTEGER NOT NULL, SolveDurationMs INTEGER NOT NULL, ResultSummaryJson TEXT NOT NULL,
                CreatedAtUtc INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SchedulerDiagnostics_RevisionId ON SchedulerDiagnostics(RevisionId);

            CREATE TABLE IF NOT EXISTS AppSettings (
                Id TEXT NOT NULL PRIMARY KEY, NotificationsEnabled INTEGER NOT NULL, AutostartEnabled INTEGER NOT NULL,
                MinimizeToTray INTEGER NOT NULL, QuickAddHotkey TEXT NOT NULL, AiProviderMode TEXT NOT NULL,
                BackupRetentionCount INTEGER NOT NULL, UpdatedAtUtc INTEGER NOT NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS AppSettings;
            DROP TABLE IF EXISTS SchedulerDiagnostics;
            DROP TABLE IF EXISTS TaskExecutions;
            """);
    }
}
