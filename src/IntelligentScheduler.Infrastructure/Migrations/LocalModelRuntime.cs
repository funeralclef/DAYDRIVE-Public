using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608180002_LocalModelRuntime")]
public sealed class LocalModelRuntime : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LocalModelId",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "balanced");
        migrationBuilder.AddColumn<bool>(
            name: "LocalModelVisionEnabled",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
        migrationBuilder.AddColumn<int>(
            name: "LocalModelIdleMinutes",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 10);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LocalModelId", table: "AppSettings");
        migrationBuilder.DropColumn(name: "LocalModelVisionEnabled", table: "AppSettings");
        migrationBuilder.DropColumn(name: "LocalModelIdleMinutes", table: "AppSettings");
    }
}
