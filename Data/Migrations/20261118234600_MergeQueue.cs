using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MergeQueueEnabled",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MergeQueueMaxBatch",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "MergeQueueRequireCi",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MergeQueueStrategy",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MergeQueueTimeoutMinutes",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MergeQueueEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    EnqueuedById = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SpeculativeSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    Builds = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RemovedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TestingSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeQueueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_AspNetUsers_EnqueuedById",
                        column: x => x.EnqueuedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_AspNetUsers_RemovedById",
                        column: x => x.RemovedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_EnqueuedById",
                table: "MergeQueueEntries",
                column: "EnqueuedById");

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_PipelineId",
                table: "MergeQueueEntries",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_PullRequestId_State",
                table: "MergeQueueEntries",
                columns: new[] { "PullRequestId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_RemovedById",
                table: "MergeQueueEntries",
                column: "RemovedById");

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_RepositoryId_TargetBranch_State_Position",
                table: "MergeQueueEntries",
                columns: new[] { "RepositoryId", "TargetBranch", "State", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "MergeQueueEnabled",
                table: "BranchProtectionRules");

            migrationBuilder.DropColumn(
                name: "MergeQueueMaxBatch",
                table: "BranchProtectionRules");

            migrationBuilder.DropColumn(
                name: "MergeQueueRequireCi",
                table: "BranchProtectionRules");

            migrationBuilder.DropColumn(
                name: "MergeQueueStrategy",
                table: "BranchProtectionRules");

            migrationBuilder.DropColumn(
                name: "MergeQueueTimeoutMinutes",
                table: "BranchProtectionRules");
        }
    }
}
