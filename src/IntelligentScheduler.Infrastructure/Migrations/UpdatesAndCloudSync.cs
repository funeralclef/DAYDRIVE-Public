using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Release checks and cloud mirroring. Both are opt-in state that belongs next to the rest of
/// the preferences, so neither needs a table of its own.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608260001_UpdatesAndCloudSync")]
public sealed class UpdatesAndCloudSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "UpdateCheckEnabled", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>(
            name: "UpdateSkippedVersion", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<long>(
            name: "UpdateLastCheckedUtc", table: "AppSettings", type: "INTEGER", nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CloudSyncProvider", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "None");
        migrationBuilder.AddColumn<string>(
            name: "CloudSyncFolderPath", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<bool>(
            name: "CloudSyncAutomatic", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<long>(
            name: "CloudSyncLastSyncUtc", table: "AppSettings", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "CloudSyncLastRevision", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "CloudSyncDeviceName", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "GoogleDriveClientId", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "GoogleDriveClientSecretCiphertext", table: "AppSettings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "GoogleDriveRefreshTokenCiphertext", table: "AppSettings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "GoogleDriveAccount", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var column in new[]
                 {
                     "UpdateCheckEnabled", "UpdateSkippedVersion", "UpdateLastCheckedUtc",
                     "CloudSyncProvider", "CloudSyncFolderPath", "CloudSyncAutomatic",
                     "CloudSyncLastSyncUtc", "CloudSyncLastRevision", "CloudSyncDeviceName",
                     "GoogleDriveClientId", "GoogleDriveClientSecretCiphertext",
                     "GoogleDriveRefreshTokenCiphertext", "GoogleDriveAccount"
                 })
            migrationBuilder.DropColumn(name: column, table: "AppSettings");
    }
}
