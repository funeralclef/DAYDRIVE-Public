using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Shortcuts the user can change.
///
/// There was one hotkey before this, written down in a column of its own and then ignored: the
/// shell registered a constant. The whole set lives in one JSON column instead of a column per
/// action, because these are a user's own bindings rather than schema — nothing queries them,
/// nothing joins on them, and adding an action should not be a migration.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609050001_ConfigurableShortcuts")]
public sealed class ConfigurableShortcuts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "ShortcutsJson",
            table: "AppSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: string.Empty);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "ShortcutsJson", table: "AppSettings");
}
