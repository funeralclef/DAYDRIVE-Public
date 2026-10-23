using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608240001_ChatHistoryAndMemory")]
public sealed class ChatHistoryAndMemory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ChatConversations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Preview = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false, defaultValue: ""),
                MessageCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                IsPinned = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                IsArchived = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ChatConversations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ChatMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", nullable: false),
                Content = table.Column<string>(type: "TEXT", nullable: false),
                Meta = table.Column<string>(type: "TEXT", nullable: true),
                AttachmentName = table.Column<string>(type: "TEXT", nullable: true),
                ToolTraceJson = table.Column<string>(type: "TEXT", nullable: true),
                IsError = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatMessages", x => x.Id);
                table.ForeignKey(
                    name: "FK_ChatMessages_ChatConversations_ConversationId",
                    column: x => x.ConversationId,
                    principalTable: "ChatConversations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserMemoryEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                Value = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: false),
                IsFromAssistant = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_UserMemoryEntries", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ChatConversations_UpdatedAtUtc",
            table: "ChatConversations",
            column: "UpdatedAtUtc");
        migrationBuilder.CreateIndex(
            name: "IX_ChatMessages_ConversationId_Ordinal",
            table: "ChatMessages",
            columns: ["ConversationId", "Ordinal"]);
        migrationBuilder.CreateIndex(
            name: "IX_UserMemoryEntries_Key",
            table: "UserMemoryEntries",
            column: "Key",
            unique: true);

        migrationBuilder.AddColumn<bool>(
            name: "McpToolsEnabled", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(
            name: "AssistantMemoryEnabled", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<Guid>(
            name: "ActiveConversationId", table: "AppSettings", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ChatMessages");
        migrationBuilder.DropTable(name: "ChatConversations");
        migrationBuilder.DropTable(name: "UserMemoryEntries");
        migrationBuilder.DropColumn(name: "McpToolsEnabled", table: "AppSettings");
        migrationBuilder.DropColumn(name: "AssistantMemoryEnabled", table: "AppSettings");
        migrationBuilder.DropColumn(name: "ActiveConversationId", table: "AppSettings");
    }
}
