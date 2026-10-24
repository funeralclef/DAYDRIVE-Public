using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// A quiet spell that does not end on its own.
///
/// "Do not disturb" was stored only as the moment it lapses, which can say "until half past
/// two" but has no way of saying "until I turn it back on" — the one answer somebody sitting
/// down to concentrate actually wants. Writing that as a moment far in the future would have
/// worked and read as a lie: the interface has to be able to tell the two apart to word the
/// line under it, so it is a state of its own.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608290004_DoNotDisturbUntilResumed")]
public sealed class DoNotDisturbUntilResumed : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<bool>(
            name: "DoNotDisturbUntilResumed",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "DoNotDisturbUntilResumed", table: "AppSettings");
}
