using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608230001_EventScheduleRepair")]
public sealed class EventScheduleRepair : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS CalendarEvents (
                Id TEXT NOT NULL PRIMARY KEY,
                Title TEXT NOT NULL,
                StartUtc INTEGER NOT NULL,
                EndUtc INTEGER NOT NULL,
                ExternalId TEXT NULL,
                Source TEXT NOT NULL,
                CreatedAtUtc INTEGER NOT NULL,
                UpdatedAtUtc INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CalendarEvents_StartUtc_EndUtc
                ON CalendarEvents(StartUtc, EndUtc);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_CalendarEvents_Source_ExternalId
                ON CalendarEvents(Source, ExternalId);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // This is a repair migration. Rolling it back must not delete user events.
    }
}