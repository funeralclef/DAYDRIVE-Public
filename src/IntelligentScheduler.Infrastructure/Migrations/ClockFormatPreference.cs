using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608170005_ClockFormatPreference")]
public sealed class ClockFormatPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<bool>(
            name: "Use24HourClock",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "Use24HourClock", table: "AppSettings");
}
