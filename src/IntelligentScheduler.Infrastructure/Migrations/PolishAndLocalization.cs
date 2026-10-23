using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608170004_PolishAndLocalization")]
public sealed class PolishAndLocalization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "LanguageCode", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "ru");
        migrationBuilder.AddColumn<bool>(name: "ReduceMotion", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "StartHidden", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "KeepOnTop", table: "AppSettings", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>(name: "LastSection", table: "AppSettings", type: "TEXT", nullable: false, defaultValue: "plan");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LanguageCode", table: "AppSettings");
        migrationBuilder.DropColumn(name: "ReduceMotion", table: "AppSettings");
        migrationBuilder.DropColumn(name: "StartHidden", table: "AppSettings");
        migrationBuilder.DropColumn(name: "KeepOnTop", table: "AppSettings");
        migrationBuilder.DropColumn(name: "LastSection", table: "AppSettings");
    }
}
