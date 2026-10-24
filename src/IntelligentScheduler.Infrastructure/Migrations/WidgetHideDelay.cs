using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// How long the quick widget stays on screen after the pointer leaves it. It used to be a
/// constant tuned for a widget that appeared on hover; opening it deliberately changes what
/// "long enough" means, so it became a preference.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608270001_WidgetHideDelay")]
public sealed class WidgetHideDelay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<int>(
            name: "WidgetHideDelayMs",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1500);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "WidgetHideDelayMs", table: "AppSettings");
}
