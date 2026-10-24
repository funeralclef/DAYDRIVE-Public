using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608170003_EdgeShellRedesign")]
public sealed class EdgeShellRedesign : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "AssistantProvider", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "Local");
        migrationBuilder.AddColumn<string>(name: "AssistantEndpoint", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "http://localhost:11434");
        migrationBuilder.AddColumn<string>(name: "AssistantModel", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "qwen3:4b");
        migrationBuilder.AddColumn<string>(name: "AssistantApiKeyCiphertext", table: "AppSettings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "McpTransport", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "StreamableHttp");
        migrationBuilder.AddColumn<string>(name: "McpCommand", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "McpArguments", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "McpToolName", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "assistant");
        migrationBuilder.AddColumn<string>(name: "ShellTheme", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "Noctalia");
        migrationBuilder.AddColumn<string>(name: "ThemeColorSource", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "Preset");
        migrationBuilder.AddColumn<string>(name: "CustomAccent", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "#8B7CF6");
        migrationBuilder.AddColumn<string>(name: "CustomBackground", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "#11131A");
        migrationBuilder.AddColumn<bool>(name: "EnableTransparency", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>(name: "RevealMode", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "Hover");
        migrationBuilder.AddColumn<bool>(name: "PinOpen", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "AutoPlan", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var column in new[]
        {
            "AssistantProvider", "AssistantEndpoint", "AssistantModel", "AssistantApiKeyCiphertext", "McpTransport",
            "McpCommand", "McpArguments", "McpToolName", "ShellTheme", "ThemeColorSource", "CustomAccent",
            "CustomBackground", "EnableTransparency", "RevealMode", "PinOpen", "AutoPlan"
        }) migrationBuilder.DropColumn(name: column, table: "AppSettings");
    }
}
