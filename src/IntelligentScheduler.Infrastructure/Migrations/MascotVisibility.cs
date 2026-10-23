using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Whether the quick widget draws its mascot. Existing profiles keep the creature they already
/// have; turning it off is a choice, not a default.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608290001_MascotVisibility")]
public sealed class MascotVisibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<bool>(
            name: "ShowMascot",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "ShowMascot", table: "AppSettings");
}
