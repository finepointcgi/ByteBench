using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Pipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CommitMessage = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggeredById = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pipelines_AspNetUsers_TriggeredById",
                        column: x => x.TriggeredById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Image = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Needs = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineJobs_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Run = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineSteps_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineStepLogs",
                columns: table => new
                {
                    StepId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStepLogs", x => x.StepId);
                    table.ForeignKey(
                        name: "FK_PipelineStepLogs_PipelineSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "PipelineSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineJobs_PipelineId",
                table: "PipelineJobs",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_PullRequestId",
                table: "Pipelines",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_RepositoryId_CommitSha",
                table: "Pipelines",
                columns: new[] { "RepositoryId", "CommitSha" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_RepositoryId_Id",
                table: "Pipelines",
                columns: new[] { "RepositoryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_Status",
                table: "Pipelines",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_TriggeredById",
                table: "Pipelines",
                column: "TriggeredById");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSteps_JobId",
                table: "PipelineSteps",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineStepLogs");

            migrationBuilder.DropTable(
                name: "PipelineSteps");

            migrationBuilder.DropTable(
                name: "PipelineJobs");

            migrationBuilder.DropTable(
                name: "Pipelines");
        }
    }
}
