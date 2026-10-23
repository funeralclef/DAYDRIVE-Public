using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608240004_AppTypography")]
public sealed class AppTypography : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "AppFontFamily",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "builtin:Inter");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "AppFontFamily", table: "AppSettings");
}
