using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// A colour for the quick widget's mascot. Empty, which is what every existing profile gets,
/// means the creature keeps following the accent colour of whatever theme is on.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608280001_MascotColor")]
public sealed class MascotColor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "MascotColor",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: string.Empty);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "MascotColor", table: "AppSettings");
}
