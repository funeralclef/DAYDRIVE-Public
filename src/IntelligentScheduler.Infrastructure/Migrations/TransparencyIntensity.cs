using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608170006_TransparencyIntensity")]
public sealed class TransparencyIntensity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<double>(
            name: "TransparencyIntensity",
            table: "AppSettings",
            type: "REAL",
            nullable: false,
            defaultValue: 45d);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "TransparencyIntensity", table: "AppSettings");
}
