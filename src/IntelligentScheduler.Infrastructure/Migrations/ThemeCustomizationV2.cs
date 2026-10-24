using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608250001_ThemeCustomizationV2")]
public sealed class ThemeCustomizationV2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "ThemeColorOverridesJson",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "{}");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "ThemeColorOverridesJson", table: "AppSettings");
}
