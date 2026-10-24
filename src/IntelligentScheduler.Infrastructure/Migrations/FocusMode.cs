using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Getting out of the way while something else has the screen.
///
/// Three columns rather than one, because the two halves of standing back are genuinely separate
/// answers. Letting go of the desktop-wide shortcuts is about a game receiving the keys it is
/// bound to; holding the reminders is about a card not being drawn over a cutscene. Somebody who
/// wants their shortcuts back in a game — a streamer starting and stopping a session between
/// rounds — should not have to give up the second to get the first.
///
/// All three default to on. The shell claims keys from the whole desktop and draws on top of
/// everything, and on a machine that is also used for games both of those are wrong often enough
/// that the useful default is the considerate one.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609160001_FocusMode")]
public sealed class FocusMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "FocusModeEnabled",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "FocusModeReleasesShortcuts",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "FocusModeHoldsReminders",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "FocusModeEnabled", table: "AppSettings");
        migrationBuilder.DropColumn(name: "FocusModeReleasesShortcuts", table: "AppSettings");
        migrationBuilder.DropColumn(name: "FocusModeHoldsReminders", table: "AppSettings");
    }
}
