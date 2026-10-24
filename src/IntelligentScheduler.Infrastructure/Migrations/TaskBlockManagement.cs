using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("202608180001_TaskBlockManagement")]
public sealed class TaskBlockManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaskScheduleExclusions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskScheduleExclusions", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskScheduleExclusions_Tasks_TaskId",
                    column: x => x.TaskId,
                    principalTable: "Tasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TaskScheduleExclusions_TaskId_StartUtc_EndUtc",
            table: "TaskScheduleExclusions",
            columns: new[] { "TaskId", "StartUtc", "EndUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "TaskScheduleExclusions");
}
