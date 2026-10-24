using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Remembers what the synced data looked like at the last exchange, so a sync can tell that
/// something was deleted. Newest-stamp comparison alone saw an added or edited row and nothing
/// else: a task deleted on one machine stayed "unchanged" there, and the next download from the
/// other machine put it back.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609110001_CloudSyncTracking")]
public sealed class CloudSyncTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CloudSyncLocalFingerprint", table: "AppSettings", type: "TEXT",
            nullable: false, defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CloudSyncLocalFingerprint", table: "AppSettings");
    }
}
