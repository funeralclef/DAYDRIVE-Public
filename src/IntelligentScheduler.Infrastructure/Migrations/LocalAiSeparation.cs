using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// The local model gets request settings of its own, and a switch that lets the machine choose
/// its engine numbers.
///
/// One set of numbers used to shape every request, whether it went to a 9B model on this
/// machine's own card or to a hosted API — so tuning it for one made it wrong for the other.
/// Whatever the user had set applied to both until now, so both start from it.
///
/// LocalModelQuality stays in the table unread: every catalogue model now comes in Q5_K_M only,
/// and rebuilding the settings table to drop one column is not worth the risk it carries.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609110002_LocalAiSeparation")]
public sealed class LocalAiSeparation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "LocalModelAutoTune", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<double>(
            name: "LocalTemperature", table: "AppSettings", type: "REAL", nullable: false, defaultValue: 0.4);
        migrationBuilder.AddColumn<double>(
            name: "LocalTopP", table: "AppSettings", type: "REAL", nullable: false, defaultValue: 0.9);
        migrationBuilder.AddColumn<int>(
            name: "LocalMaxTokens", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 3000);
        migrationBuilder.AddColumn<int>(
            name: "LocalMaxToolIterations", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 8);
        migrationBuilder.AddColumn<int>(
            name: "LocalHistoryTurns", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 16);
        migrationBuilder.AddColumn<int>(
            name: "LocalTimeoutMinutes", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 10);

        migrationBuilder.Sql(
            """
            UPDATE AppSettings
               SET LocalTemperature = AssistantTemperature,
                   LocalTopP = AssistantTopP,
                   LocalMaxTokens = AssistantMaxTokens,
                   LocalMaxToolIterations = AssistantMaxToolIterations,
                   LocalHistoryTurns = AssistantHistoryTurns,
                   LocalTimeoutMinutes = AssistantTimeoutMinutes;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var column in new[]
                 {
                     "LocalModelAutoTune", "LocalTemperature", "LocalTopP", "LocalMaxTokens",
                     "LocalMaxToolIterations", "LocalHistoryTurns", "LocalTimeoutMinutes"
                 })
            migrationBuilder.DropColumn(name: column, table: "AppSettings");
    }
}
