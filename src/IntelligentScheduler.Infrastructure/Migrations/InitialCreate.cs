using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608170001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS Tasks (
                Id TEXT NOT NULL PRIMARY KEY, Title TEXT NOT NULL, Notes TEXT NULL, Status TEXT NOT NULL,
                Priority INTEGER NOT NULL, EstimatedDurationMin INTEGER NOT NULL, RemainingDurationMin INTEGER NOT NULL,
                DeadlineUtc TEXT NULL, EarliestStartUtc TEXT NULL, Splittable INTEGER NOT NULL,
                MinBlockMin INTEGER NOT NULL, MaxSplits INTEGER NOT NULL, EnergyRequired INTEGER NULL,
                PreferredTimeOfDay TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, Version INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Tasks_Status_Priority ON Tasks(Status, Priority);

            CREATE TABLE IF NOT EXISTS CalendarEvents (
                Id TEXT NOT NULL PRIMARY KEY, Title TEXT NOT NULL, StartUtc TEXT NOT NULL, EndUtc TEXT NOT NULL,
                ExternalId TEXT NULL, Source TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CalendarEvents_StartUtc_EndUtc ON CalendarEvents(StartUtc, EndUtc);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CalendarEvents_Source_ExternalId ON CalendarEvents(Source, ExternalId);

            CREATE TABLE IF NOT EXISTS PlanRevisions (
                Id TEXT NOT NULL PRIMARY KEY, HorizonStartUtc TEXT NOT NULL, HorizonEndUtc TEXT NOT NULL,
                Status TEXT NOT NULL, ObjectiveScore INTEGER NOT NULL, SolveDurationMs INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TimeBlocks (
                Id TEXT NOT NULL PRIMARY KEY, TaskId TEXT NULL, TitleOverride TEXT NULL, StartUtc TEXT NOT NULL, EndUtc TEXT NOT NULL,
                State TEXT NOT NULL, Origin TEXT NOT NULL, IsLocked INTEGER NOT NULL, PlanRevisionId TEXT NULL,
                PreviousStartUtc TEXT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY(TaskId) REFERENCES Tasks(Id) ON DELETE SET NULL,
                FOREIGN KEY(PlanRevisionId) REFERENCES PlanRevisions(Id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TimeBlocks_StartUtc_EndUtc ON TimeBlocks(StartUtc, EndUtc);
            CREATE INDEX IF NOT EXISTS IX_TimeBlocks_TaskId ON TimeBlocks(TaskId);

            CREATE TABLE IF NOT EXISTS AvailabilityRules (
                Id TEXT NOT NULL PRIMARY KEY, DayOfWeek INTEGER NOT NULL, StartLocal TEXT NOT NULL, EndLocal TEXT NOT NULL, IsEnabled INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AvailabilityExceptions (
                Id TEXT NOT NULL PRIMARY KEY, Date TEXT NOT NULL, StartLocal TEXT NOT NULL, EndLocal TEXT NOT NULL, Kind TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS PlanningProfiles (
                Id TEXT NOT NULL PRIMARY KEY, TimeZoneId TEXT NOT NULL, SchedulingUnitMinutes INTEGER NOT NULL,
                BufferBeforeFixedMin INTEGER NOT NULL, BufferAfterFixedMin INTEGER NOT NULL, MaxSolveTimeMs INTEGER NOT NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS TimeBlocks;
            DROP TABLE IF EXISTS PlanRevisions;
            DROP TABLE IF EXISTS CalendarEvents;
            DROP TABLE IF EXISTS AvailabilityExceptions;
            DROP TABLE IF EXISTS AvailabilityRules;
            DROP TABLE IF EXISTS PlanningProfiles;
            DROP TABLE IF EXISTS Tasks;
            """);
    }
}
