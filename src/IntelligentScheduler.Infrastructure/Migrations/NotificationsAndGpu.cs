using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Reminders that belong to the application, and a model that may use the graphics card.
///
/// Notifications used to be handed to the operating system, which put them wherever it keeps
/// such things and styled them like everything else there. They are shown by the shell now, so
/// the two questions the system used to answer for us have to be answered here: whether they
/// make a sound, and whether they arrive at all right now. "Do not disturb" is stored as the
/// moment it lapses rather than as a flag, so a quiet hour survives a restart and ends on its
/// own; the minute count beside it is only the length the next one is started with.
///
/// The graphics switch defaults to on. Anyone who already downloaded the processor build keeps
/// it — the two engines live side by side — and the card build is fetched the first time a
/// model is actually started.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608290003_NotificationsAndGpu")]
public sealed class NotificationsAndGpu : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NotificationSoundEnabled",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<int>(
            name: "DoNotDisturbMinutes",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 60);

        migrationBuilder.AddColumn<long>(
            name: "DoNotDisturbUntilUtc",
            table: "AppSettings",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "LocalModelGpuEnabled",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "NotificationSoundEnabled", table: "AppSettings");
        migrationBuilder.DropColumn(name: "DoNotDisturbMinutes", table: "AppSettings");
        migrationBuilder.DropColumn(name: "DoNotDisturbUntilUtc", table: "AppSettings");
        migrationBuilder.DropColumn(name: "LocalModelGpuEnabled", table: "AppSettings");
    }
}
