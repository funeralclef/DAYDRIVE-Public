using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Projects: a large piece of work written down as steps, each with the length and the day the
/// user gives it.
///
/// A step's day is stored as a plain date and not as a moment, because it is not one — "on
/// Thursday" is the whole of the plan until the step is booked at an hour. The booking is the
/// ordinary calendar event it points at, which is why the link is a bare id rather than a key:
/// deleting the event is allowed to leave the step behind, planned for its day again.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609150001_ProjectsAndSteps")]
public sealed class ProjectsAndSteps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Work"),
                Deadline = table.Column<DateOnly>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Projects", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ProjectSteps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 60),
                PlannedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                IsDone = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                EventId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectSteps", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProjectSteps_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProjectSteps_ProjectId_SortOrder", table: "ProjectSteps",
            columns: new[] { "ProjectId", "SortOrder" });
        migrationBuilder.CreateIndex(
            name: "IX_ProjectSteps_PlannedDate", table: "ProjectSteps", column: "PlannedDate");
        migrationBuilder.CreateIndex(
            name: "IX_ProjectSteps_EventId", table: "ProjectSteps", column: "EventId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProjectSteps");
        migrationBuilder.DropTable(name: "Projects");
    }
}
