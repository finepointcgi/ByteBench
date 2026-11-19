using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CommitSha",
                table: "PullRequestReviews",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BranchProtectionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    RequiredApprovals = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockDirectPushes = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockForcePushes = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockDeletion = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireChecksToPass = table.Column<bool>(type: "INTEGER", nullable: false),
                    DismissStaleApprovals = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowAdminBypass = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultReviewerIds = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchProtectionRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchProtectionRules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "PullRequestReviewers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestReviewers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_AspNetUsers_AddedById",
                        column: x => x.AddedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryRedirects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    OldSlug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryRedirects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryRedirects_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Events = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    DisabledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDeliveryAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhooks_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Webhooks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
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
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebhookId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Event = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    IsRedelivery = table.Column<bool>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseBody = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
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
                name: "IX_PersonalAccessTokens_TokenHash",
                table: "PersonalAccessTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_BranchProtectionRules_RepositoryId_Pattern",
                table: "BranchProtectionRules",
                columns: new[] { "RepositoryId", "Pattern" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_AddedById",
                table: "PullRequestReviewers",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_PullRequestId_UserId",
                table: "PullRequestReviewers",
                columns: new[] { "PullRequestId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_UserId",
                table: "PullRequestReviewers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRedirects_ProjectId_OldSlug",
                table: "RepositoryRedirects",
                columns: new[] { "ProjectId", "OldSlug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRedirects_RepositoryId",
                table: "RepositoryRedirects",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WebhookId_CreatedAt",
                table: "WebhookDeliveries",
                columns: new[] { "WebhookId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_CreatedById",
                table: "Webhooks",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_ProjectId",
                table: "Webhooks",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchProtectionRules");

            migrationBuilder.DropTable(
                name: "PipelineStepLogs");

            migrationBuilder.DropTable(
                name: "PullRequestReviewers");

            migrationBuilder.DropTable(
                name: "RepositoryRedirects");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "PipelineSteps");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropTable(
                name: "PipelineJobs");

            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_PersonalAccessTokens_TokenHash",
                table: "PersonalAccessTokens");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "CommitSha",
                table: "PullRequestReviews");
        }
    }
}
