using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608240005_ThemeModesAndMarelian")]
public sealed class ThemeModesAndMarelian : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShellThemeMode",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "Dark");

        migrationBuilder.Sql("UPDATE AppSettings SET ShellTheme = 'Marelian' WHERE ShellTheme = 'Custom';");
        migrationBuilder.Sql("UPDATE AppSettings SET ShellThemeMode = 'Light' WHERE ShellTheme = 'Celestia';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE AppSettings SET ShellTheme = 'Custom' WHERE ShellTheme = 'Marelian';");
        migrationBuilder.DropColumn(name: "ShellThemeMode", table: "AppSettings");
    }
}
