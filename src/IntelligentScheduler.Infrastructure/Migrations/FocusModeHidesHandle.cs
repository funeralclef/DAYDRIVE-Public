using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// The third half of standing back: the petal leaving the edge of the screen.
///
/// Letting go of the shortcuts and holding the reminders between them cover everything the shell
/// <em>sends</em> while a game has the screen. They cover nothing at all about what it still
/// <em>occupies</em> — and the petal is a strip of the right-hand edge that goes on swallowing
/// clicks, which is precisely where a mouse ends up when it is flung at something in a hurry. A
/// focus mode that quietly lets the shell keep a piece of the screen is not one.
///
/// On by default, like the other two, and for the same reason. It is a switch rather than a
/// consequence because the petal is also the way back to a hidden shell, and somebody who has
/// no tray icon on their desktop should be able to keep it.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609170001_FocusModeHidesHandle")]
public sealed class FocusModeHidesHandle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<bool>(
            name: "FocusModeHidesHandle",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "FocusModeHidesHandle", table: "AppSettings");
}
