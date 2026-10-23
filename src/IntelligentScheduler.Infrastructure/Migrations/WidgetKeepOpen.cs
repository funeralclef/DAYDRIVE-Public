using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// A quick widget that does not put itself away.
///
/// The grace period next to this is an answer to "was this still wanted?", asked of a widget
/// that appeared beside the pointer on the way past. Somebody who opens it deliberately and
/// works next to it is answering that question every time they glance at it, and a widget that
/// keeps fading out from under a glance is one they have to keep re-opening. Off by default:
/// the widget is still a glance for most people, and a surface that never leaves has to be
/// asked for.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609020001_WidgetKeepOpen")]
public sealed class WidgetKeepOpen : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<bool>(
            name: "WidgetKeepOpen",
            table: "AppSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "WidgetKeepOpen", table: "AppSettings");
}
