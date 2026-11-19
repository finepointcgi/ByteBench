using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Wave5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PullRequestComments_PullRequestId",
                table: "PullRequestComments");

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId",
                table: "Webhooks",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Webhooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AiDismissed",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiFingerprint",
                table: "PullRequestComments",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiReviewId",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiSeverity",
                table: "PullRequestComments",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AiTriageAutoApplyLabels",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AiTriageEnabled",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DoraFailureWindowHours",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProductionEnvironment",
                table: "Projects",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AwaitingApproval",
                table: "MergeQueueEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AwaitingApprovalSince",
                table: "MergeQueueEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeleteSourceBranch",
                table: "MergeQueueEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MergeQueueApprovalTimeoutMinutes",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Backend",
                table: "AiReviews",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiffChars",
                table: "AiReviews",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ChatIntegrationOptOuts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    WebhookId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatIntegrationOptOuts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatIntegrationOptOuts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatIntegrationOptOuts_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureFlags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OffValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IssueNumbers = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureFlags_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FeatureFlags_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagEnvironments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    MembersJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagSegments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTriageRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RequestedById = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTriageRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTriageRuns_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PagesSites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    NotFound = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewSites = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReviewExpireDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagesSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagesSites_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryAiReviewSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxDiffChars = table.Column<int>(type: "INTEGER", nullable: false),
                    IgnorePaths = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    FocusPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    BlockOnUnresolvedFindings = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedById = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryAiReviewSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryAiReviewSettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    FlagId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    EnvironmentName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ActorId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagChanges_FeatureFlags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagEnvironmentStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FlagId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    LastEvaluatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagEnvironmentStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_FeatureFlags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_FlagEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "FlagEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagSdkKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    RotatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagSdkKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagSdkKeys_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlagSdkKeys_FlagEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "FlagEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTriageItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: true),
                    Value = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedById = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTriageItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTriageItems_IssueTriageRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "IssueTriageRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PagesDeployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeployedById = table.Column<int>(type: "INTEGER", nullable: true),
                    Directory = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRollback = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagesDeployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagesDeployments_AspNetUsers_DeployedById",
                        column: x => x.DeployedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PagesDeployments_PagesSites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "PagesSites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_GroupId",
                table: "Webhooks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_PullRequestId_AiFingerprint",
                table: "PullRequestComments",
                columns: new[] { "PullRequestId", "AiFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatIntegrationOptOuts_ProjectId_WebhookId",
                table: "ChatIntegrationOptOuts",
                columns: new[] { "ProjectId", "WebhookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatIntegrationOptOuts_WebhookId",
                table: "ChatIntegrationOptOuts",
                column: "WebhookId");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_CreatedById",
                table: "FeatureFlags",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_ProjectId_Key",
                table: "FeatureFlags",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagChanges_FlagId_At",
                table: "FlagChanges",
                columns: new[] { "FlagId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironments_ProjectId_Name",
                table: "FlagEnvironments",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_EnvironmentId",
                table: "FlagEnvironmentStates",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_FlagId_EnvironmentId",
                table: "FlagEnvironmentStates",
                columns: new[] { "FlagId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_UpdatedById",
                table: "FlagEnvironmentStates",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_CreatedById",
                table: "FlagSdkKeys",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_EnvironmentId",
                table: "FlagSdkKeys",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_KeyHash",
                table: "FlagSdkKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagSegments_ProjectId_Key",
                table: "FlagSegments",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueTriageItems_RunId",
                table: "IssueTriageItems",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTriageRuns_IssueId_CreatedAt",
                table: "IssueTriageRuns",
                columns: new[] { "IssueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_DeployedById",
                table: "PagesDeployments",
                column: "DeployedById");

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_ExpiresAt",
                table: "PagesDeployments",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_SiteId_Slot_IsCurrent",
                table: "PagesDeployments",
                columns: new[] { "SiteId", "Slot", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_SiteId_Slot_Version",
                table: "PagesDeployments",
                columns: new[] { "SiteId", "Slot", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PagesSites_RepositoryId",
                table: "PagesSites",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryAiReviewSettings_RepositoryId",
                table: "RepositoryAiReviewSettings",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Webhooks_Groups_GroupId",
                table: "Webhooks",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Webhooks_Groups_GroupId",
                table: "Webhooks");

            migrationBuilder.DropTable(
                name: "ChatIntegrationOptOuts");

            migrationBuilder.DropTable(
                name: "FlagChanges");

            migrationBuilder.DropTable(
                name: "FlagEnvironmentStates");

            migrationBuilder.DropTable(
                name: "FlagSdkKeys");

            migrationBuilder.DropTable(
                name: "FlagSegments");

            migrationBuilder.DropTable(
                name: "IssueTriageItems");

            migrationBuilder.DropTable(
                name: "PagesDeployments");

            migrationBuilder.DropTable(
                name: "RepositoryAiReviewSettings");

            migrationBuilder.DropTable(
                name: "FeatureFlags");

            migrationBuilder.DropTable(
                name: "FlagEnvironments");

            migrationBuilder.DropTable(
                name: "IssueTriageRuns");

            migrationBuilder.DropTable(
                name: "PagesSites");

            migrationBuilder.DropIndex(
                name: "IX_Webhooks_GroupId",
                table: "Webhooks");

            migrationBuilder.DropIndex(
                name: "IX_PullRequestComments_PullRequestId_AiFingerprint",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "AiDismissed",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiFingerprint",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiReviewId",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiSeverity",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiTriageAutoApplyLabels",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AiTriageEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DoraFailureWindowHours",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ProductionEnvironment",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AwaitingApproval",
                table: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "AwaitingApprovalSince",
                table: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "DeleteSourceBranch",
                table: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "MergeQueueApprovalTimeoutMinutes",
                table: "BranchProtectionRules");

            migrationBuilder.DropColumn(
                name: "Backend",
                table: "AiReviews");

            migrationBuilder.DropColumn(
                name: "DiffChars",
                table: "AiReviews");

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId",
                table: "Webhooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_PullRequestId",
                table: "PullRequestComments",
                column: "PullRequestId");
        }
    }
}
