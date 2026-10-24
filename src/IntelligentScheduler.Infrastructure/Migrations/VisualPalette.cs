using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608240003_VisualPalette")]
public sealed class VisualPalette : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "VisualPalette",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "Vibrant");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "VisualPalette", table: "AppSettings");
}