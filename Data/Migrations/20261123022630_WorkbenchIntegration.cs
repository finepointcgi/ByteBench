using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkbenchIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WorkbenchPushEnabled",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkbenchConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ApiKeyProtected = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    KeyHint = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    KeySetAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    KeySetById = table.Column<int>(type: "INTEGER", nullable: true),
                    LastTestAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTestOk = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchIssueLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WbsCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LastTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Missing = table.Column<bool>(type: "INTEGER", nullable: false),
                    MissingSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchIssueLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchIssueLinks_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkbenchIssueLinks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    WbsCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WorkbenchProjectCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Percent = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchOutbox_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchPlanSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    RootWbsCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    EpicLevels = table.Column<int>(type: "INTEGER", nullable: false),
                    ReconcileEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Processed = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchPlanSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchPlanSyncs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchTaskSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    WbsCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LastPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastPushedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchTaskSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchTaskSyncs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchIssueLinks_IssueId",
                table: "WorkbenchIssueLinks",
                column: "IssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchIssueLinks_ProjectId_TaskId",
                table: "WorkbenchIssueLinks",
                columns: new[] { "ProjectId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchOutbox_ProjectId_CreatedAt",
                table: "WorkbenchOutbox",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchOutbox_State_NextAttemptAt",
                table: "WorkbenchOutbox",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchPlanSyncs_ProjectId",
                table: "WorkbenchPlanSyncs",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchTaskSyncs_ProjectId_WbsCode",
                table: "WorkbenchTaskSyncs",
                columns: new[] { "ProjectId", "WbsCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkbenchConnections");

            migrationBuilder.DropTable(
                name: "WorkbenchIssueLinks");

            migrationBuilder.DropTable(
                name: "WorkbenchOutbox");

            migrationBuilder.DropTable(
                name: "WorkbenchPlanSyncs");

            migrationBuilder.DropTable(
                name: "WorkbenchTaskSyncs");

            migrationBuilder.DropColumn(
                name: "WorkbenchPushEnabled",
                table: "Projects");
        }
    }
}
