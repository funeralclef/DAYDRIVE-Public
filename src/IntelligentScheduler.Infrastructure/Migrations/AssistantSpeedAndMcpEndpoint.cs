using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// An address of its own for the MCP server, and request limits worth the models people
/// actually connect.
///
/// MCP used to borrow <c>AssistantEndpoint</c>, the same field the chat API writes its URL
/// into. Tool federation is on by default and only asks whether that field parses as a URL, so
/// pointing the app at any hosted API silently declared an MCP server at the same address — and
/// every single message then opened, failed and tore down a Model Context Protocol session
/// against a chat endpoint before the question was even sent. That is seconds per turn, paid on
/// "what time is it" as much as on a week of planning. The two addresses are separate rows now;
/// a profile already using MCP keeps its endpoint by copying it across.
///
/// The parameter bump only touches rows still sitting on the shipped values: someone who chose
/// 2000 tokens deliberately keeps 2000.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202608290002_AssistantSpeedAndMcpEndpoint")]
public sealed class AssistantSpeedAndMcpEndpoint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "McpEndpoint",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.Sql(
            """
            UPDATE AppSettings
               SET McpEndpoint = AssistantEndpoint
             WHERE AssistantProvider = 'Mcp' AND (McpEndpoint IS NULL OR McpEndpoint = '');
            """);

        migrationBuilder.Sql(
            """
            UPDATE AppSettings
               SET AssistantMaxTokens = 4000
             WHERE AssistantMaxTokens = 2000;
            """);

        migrationBuilder.Sql(
            """
            UPDATE AppSettings
               SET AssistantMaxToolIterations = 12
             WHERE AssistantMaxToolIterations = 8;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "McpEndpoint", table: "AppSettings");
}
