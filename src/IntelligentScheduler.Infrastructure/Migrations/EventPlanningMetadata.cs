using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608230002_EventPlanningMetadata")]
public sealed class EventPlanningMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Category", table: "CalendarEvents", type: "TEXT", nullable: false, defaultValue: nameof(EventCategory.General));
        migrationBuilder.AddColumn<string>(name: "Priority", table: "CalendarEvents", type: "TEXT", nullable: false, defaultValue: nameof(EventPriority.Normal));
        migrationBuilder.AddColumn<string>(name: "Flexibility", table: "CalendarEvents", type: "TEXT", nullable: false, defaultValue: nameof(EventFlexibility.Fixed));
        migrationBuilder.AddColumn<string>(name: "Recurrence", table: "CalendarEvents", type: "TEXT", nullable: false, defaultValue: nameof(EventRecurrence.None));
        migrationBuilder.AddColumn<Guid>(name: "SeriesId", table: "CalendarEvents", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>(name: "OccurrenceIndex", table: "CalendarEvents", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<bool>(name: "IsRecurrenceException", table: "CalendarEvents", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.CreateIndex(name: "IX_CalendarEvents_SeriesId_StartUtc", table: "CalendarEvents", columns: new[] { "SeriesId", "StartUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_CalendarEvents_SeriesId_StartUtc", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "Category", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "Priority", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "Flexibility", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "Recurrence", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "SeriesId", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "OccurrenceIndex", table: "CalendarEvents");
        migrationBuilder.DropColumn(name: "IsRecurrenceException", table: "CalendarEvents");
    }
}