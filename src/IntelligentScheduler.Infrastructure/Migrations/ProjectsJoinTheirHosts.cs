using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IntelligentScheduler.Infrastructure.Migrations;

/// <summary>
/// Projects stop being a third list and become what they always described: a task, or an event,
/// broken into steps.
///
/// A project now points at its host — the unscheduled task it breaks up, the one event it is the
/// checklist of, or the repeating series whose every occurrence carries the same list. The links
/// are bare ids for the same reason a step's slot is: the host is looked for rather than joined to,
/// and one that has gone is noticed by the shell and its steps let go of.
///
/// A scheduled project's steps are ticked per sitting, because a weekly checklist is ticked afresh
/// every week; those ticks get a table of their own. Projects written before this have no host, and
/// are given a task of their own by the application on the first launch after it — in code rather
/// than here, because the deadline they carry is a local day and the task's is a moment.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("202609180001_ProjectsJoinTheirHosts")]
public sealed class ProjectsJoinTheirHosts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "TaskId", table: "Projects", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "EventId", table: "Projects", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "SeriesId", table: "Projects", type: "TEXT", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Projects_TaskId", table: "Projects", column: "TaskId");
        migrationBuilder.CreateIndex(name: "IX_Projects_EventId", table: "Projects", column: "EventId");
        migrationBuilder.CreateIndex(name: "IX_Projects_SeriesId", table: "Projects", column: "SeriesId");

        migrationBuilder.CreateTable(
            name: "ProjectStepChecks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                StepId = table.Column<Guid>(type: "TEXT", nullable: false),
                EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ProjectStepChecks", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ProjectStepChecks_StepId_EventId", table: "ProjectStepChecks",
            columns: new[] { "StepId", "EventId" }, unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ProjectStepChecks_EventId", table: "ProjectStepChecks", column: "EventId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProjectStepChecks");
        migrationBuilder.DropIndex(name: "IX_Projects_SeriesId", table: "Projects");
        migrationBuilder.DropIndex(name: "IX_Projects_EventId", table: "Projects");
        migrationBuilder.DropIndex(name: "IX_Projects_TaskId", table: "Projects");
        migrationBuilder.DropColumn(name: "SeriesId", table: "Projects");
        migrationBuilder.DropColumn(name: "EventId", table: "Projects");
        migrationBuilder.DropColumn(name: "TaskId", table: "Projects");
    }
}
