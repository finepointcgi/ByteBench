using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentsDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentAction",
                table: "PipelineJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Action",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsRollback",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RedeployOfId",
                table: "Deployments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequiredApprovals",
                table: "CiEnvironments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "StoppedAt",
                table: "CiEnvironments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CiEnvironmentApprovers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiEnvironmentApprovers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiEnvironmentApprovers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CiEnvironmentApprovers_CiEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "CiEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CiEnvironmentApprovers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeployFreezeWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CronStart = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CronEnd = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StartsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeployFreezeWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeployFreezeWindows_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DeployFreezeWindows_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentApprovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeploymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentApprovals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeploymentApprovals_Deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DeploymentApprovals_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_CommitSha",
                table: "Deployments",
                column: "CommitSha");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_RedeployOfId",
                table: "Deployments",
                column: "RedeployOfId");

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironmentApprovers_EnvironmentId",
                table: "CiEnvironmentApprovers",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironmentApprovers_GroupId",
                table: "CiEnvironmentApprovers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironmentApprovers_UserId",
                table: "CiEnvironmentApprovers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DeployFreezeWindows_CreatedById",
                table: "DeployFreezeWindows",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_DeployFreezeWindows_ProjectId",
                table: "DeployFreezeWindows",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentApprovals_DeploymentId",
                table: "DeploymentApprovals",
                column: "DeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentApprovals_JobId_IsStale",
                table: "DeploymentApprovals",
                columns: new[] { "JobId", "IsStale" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentApprovals_UserId",
                table: "DeploymentApprovals",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_Deployments_RedeployOfId",
                table: "Deployments",
                column: "RedeployOfId",
                principalTable: "Deployments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_Deployments_RedeployOfId",
                table: "Deployments");

            migrationBuilder.DropTable(
                name: "CiEnvironmentApprovers");

            migrationBuilder.DropTable(
                name: "DeployFreezeWindows");

            migrationBuilder.DropTable(
                name: "DeploymentApprovals");

            migrationBuilder.DropIndex(
                name: "IX_Deployments_CommitSha",
                table: "Deployments");

            migrationBuilder.DropIndex(
                name: "IX_Deployments_RedeployOfId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "EnvironmentAction",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "IsRollback",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RedeployOfId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RequiredApprovals",
                table: "CiEnvironments");

            migrationBuilder.DropColumn(
                name: "StoppedAt",
                table: "CiEnvironments");
        }
    }
}
