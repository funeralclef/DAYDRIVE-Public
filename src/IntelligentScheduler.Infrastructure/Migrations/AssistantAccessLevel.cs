using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608230003_AssistantAccessLevel")]
public sealed class AssistantAccessLevel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AssistantAccessLevel",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: nameof(IntelligentScheduler.Domain.AssistantAccessLevel.ConfirmAll));
        migrationBuilder.AddColumn<bool>(
            name: "ShareContextWithExternalAi",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ShareContextWithExternalAi", table: "AppSettings");
        migrationBuilder.DropColumn(name: "AssistantAccessLevel", table: "AppSettings");
    }
}