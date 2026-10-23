using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// A timer that can be held, and a day that can be answered for in one pass.
///
/// The stopwatch used to be a single "started at" stamp, which made every interruption a
/// question: stop and lose the measurement, or leave it running and record a lunch break as
/// work. Holding the pause apart from the work means stepping away costs nothing and asks
/// nothing — the two numbers are simply kept separately.
///
/// The follow-up table is the other half. A verdict describes one occurrence; where to pick
/// the work up, and how many times it has been pushed onto a later day, describe the work
/// itself, and have to outlive the slot it was booked into.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609030001_SessionsAndDayWrapUp")]
public sealed class SessionsAndDayWrapUp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PausedSeconds", table: "EventOutcomes", type: "INTEGER",
            nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<long>(
            name: "PausedAtUtc", table: "EventOutcomes", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "ProgressPercent", table: "EventOutcomes", type: "INTEGER",
            nullable: false, defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "EventFollowUps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                NextStep = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                DeferCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                LastDeferredAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                Blocker = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "None"),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_EventFollowUps", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_EventFollowUps_EventId", table: "EventFollowUps",
            column: "EventId", unique: true);

        migrationBuilder.AddColumn<int>(
            name: "WrapUpIgnoredStreak", table: "AppSettings", type: "INTEGER",
            nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<long>(
            name: "WrapUpLastOfferedUtc", table: "AppSettings", type: "INTEGER", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "EventFollowUps");
        migrationBuilder.DropColumn(name: "PausedSeconds", table: "EventOutcomes");
        migrationBuilder.DropColumn(name: "PausedAtUtc", table: "EventOutcomes");
        migrationBuilder.DropColumn(name: "ProgressPercent", table: "EventOutcomes");
        migrationBuilder.DropColumn(name: "WrapUpIgnoredStreak", table: "AppSettings");
        migrationBuilder.DropColumn(name: "WrapUpLastOfferedUtc", table: "AppSettings");
    }
}
