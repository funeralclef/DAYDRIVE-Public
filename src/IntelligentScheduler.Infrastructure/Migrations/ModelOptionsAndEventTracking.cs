using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608240002_ModelOptionsAndEventTracking")]
public sealed class ModelOptionsAndEventTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LocalModelQuality", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "Balanced");
        migrationBuilder.AddColumn<int>(
            name: "LocalModelContextTokens", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 8192);
        migrationBuilder.AddColumn<int>(
            name: "LocalModelThreads", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(
            name: "CustomModelPath", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "CustomProjectorPath", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");

        migrationBuilder.AddColumn<double>(
            name: "AssistantTemperature", table: "AppSettings", type: "REAL", nullable: false, defaultValue: 0.3);
        migrationBuilder.AddColumn<int>(
            name: "AssistantMaxTokens", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 2000);
        migrationBuilder.AddColumn<double>(
            name: "AssistantTopP", table: "AppSettings", type: "REAL", nullable: false, defaultValue: 1.0);
        migrationBuilder.AddColumn<int>(
            name: "AssistantMaxToolIterations", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 8);
        migrationBuilder.AddColumn<int>(
            name: "AssistantHistoryTurns", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 24);
        migrationBuilder.AddColumn<int>(
            name: "AssistantTimeoutMinutes", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: 5);
        migrationBuilder.AddColumn<string>(
            name: "AssistantExtraInstructions", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");

        // The catalogue was replaced, so the stored id has to move with it.
        migrationBuilder.Sql(
            "UPDATE AppSettings SET LocalModelId = 'qwen35-9b' WHERE LocalModelId IN ('lite', 'balanced', 'smart');");

        migrationBuilder.CreateTable(
            name: "EventOutcomes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                PlannedStartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                PlannedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                FinishedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                ActualMinutes = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_EventOutcomes", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_EventOutcomes_EventId", table: "EventOutcomes", column: "EventId", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_EventOutcomes_PlannedStartUtc", table: "EventOutcomes", column: "PlannedStartUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "EventOutcomes");
        foreach (var column in new[]
                 {
                     "LocalModelQuality", "LocalModelContextTokens", "LocalModelThreads",
                     "CustomModelPath", "CustomProjectorPath",
                     "AssistantTemperature", "AssistantMaxTokens", "AssistantTopP",
                     "AssistantMaxToolIterations", "AssistantHistoryTurns", "AssistantTimeoutMinutes",
                     "AssistantExtraInstructions"
                 })
            migrationBuilder.DropColumn(name: column, table: "AppSettings");
    }
}
