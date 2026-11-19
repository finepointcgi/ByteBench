using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Wave4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChatSettings",
                table: "Webhooks",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Webhooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Webhooks",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SlackDmEnabled",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentAction",
                table: "PipelineJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChatPending",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.AddColumn<bool>(
                name: "IsPublicReply",
                table: "Comments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ServiceDeskContactId",
                table: "Comments",
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
                name: "ChatUserLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TeamId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ExternalUserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nonce = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatUserLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatUserLinks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Packages_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectPackageSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeepLatest = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectPackageSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectPackageSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositorySecuritySettings",
                columns: table => new
                {
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    MergeCheckEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MergeCheckSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    NotifySeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySecuritySettings", x => x.RepositoryId);
                    table.ForeignKey(
                        name: "FK_RepositorySecuritySettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ToolVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Critical = table.Column<int>(type: "INTEGER", nullable: false),
                    High = table.Column<int>(type: "INTEGER", nullable: false),
                    Medium = table.Column<int>(type: "INTEGER", nullable: false),
                    Low = table.Column<int>(type: "INTEGER", nullable: false),
                    Other = table.Column<int>(type: "INTEGER", nullable: false),
                    Dismissed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityScans_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SecurityScans_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityTriages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    By = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityTriages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityTriages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityTriages_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicFormEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomAddress = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    DefaultIssueType = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultLabelIds = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultAssigneeId = table.Column<int>(type: "INTEGER", nullable: true),
                    IssueTemplate = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    AutoReplyEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoReplyTemplate = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    NotifyStatusIds = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseTargetHours = table.Column<int>(type: "INTEGER", nullable: false),
                    FormIntro = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ServiceDeskSettings_AspNetUsers_DefaultAssigneeId",
                        column: x => x.DefaultAssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ServiceDeskSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContainerBlobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    IsManifest = table.Column<bool>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerBlobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContainerBlobs_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageDistTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageDistTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageDistTags_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NormalizedVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestDigest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedById = table.Column<int>(type: "INTEGER", nullable: true),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageVersions_AspNetUsers_PublishedById",
                        column: x => x.PublishedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PackageVersions_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    PackageName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PackageVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Ecosystem = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FixedVersions = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Line = table.Column<int>(type: "INTEGER", nullable: true),
                    Engines = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Aliases = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    HelpUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Context = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ReportedTriage = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityFindings_SecurityScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SecurityScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContactId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplyToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TrackingToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    InitialMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRequesterActivityAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstResponseAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAgentReplyAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_ServiceDeskContacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "ServiceDeskContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PackageFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sha1 = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Sha512 = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageFiles_PackageVersions_PackageVersionId",
                        column: x => x.PackageVersionId,
                        principalTable: "PackageVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceDeskMessages_ServiceDeskTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "ServiceDeskTickets",
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
                name: "IX_Comments_ServiceDeskContactId",
                table: "Comments",
                column: "ServiceDeskContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_Nonce",
                table: "ChatUserLinks",
                column: "Nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_Provider_TeamId_ExternalUserId",
                table: "ChatUserLinks",
                columns: new[] { "Provider", "TeamId", "ExternalUserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_UserId",
                table: "ChatUserLinks",
                column: "UserId");

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
                name: "IX_ContainerBlobs_PackageId_Sha256",
                table: "ContainerBlobs",
                columns: new[] { "PackageId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContainerBlobs_Sha256",
                table: "ContainerBlobs",
                column: "Sha256");

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

            migrationBuilder.CreateIndex(
                name: "IX_PackageDistTags_PackageId_Tag",
                table: "PackageDistTags",
                columns: new[] { "PackageId", "Tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageFiles_PackageVersionId",
                table: "PackageFiles",
                column: "PackageVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageFiles_Sha256",
                table: "PackageFiles",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ProjectId_Type_NormalizedName",
                table: "Packages",
                columns: new[] { "ProjectId", "Type", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PackageId_NormalizedVersion",
                table: "PackageVersions",
                columns: new[] { "PackageId", "NormalizedVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PublishedById",
                table: "PackageVersions",
                column: "PublishedById");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityFindings_RepositoryId_Fingerprint",
                table: "SecurityFindings",
                columns: new[] { "RepositoryId", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityFindings_ScanId_Fingerprint",
                table: "SecurityFindings",
                columns: new[] { "ScanId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityScans_PipelineId",
                table: "SecurityScans",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityScans_RepositoryId_CommitSha",
                table: "SecurityScans",
                columns: new[] { "RepositoryId", "CommitSha" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityScans_RepositoryId_Ref_PipelineId",
                table: "SecurityScans",
                columns: new[] { "RepositoryId", "Ref", "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityTriages_RepositoryId_Fingerprint",
                table: "SecurityTriages",
                columns: new[] { "RepositoryId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityTriages_UserId",
                table: "SecurityTriages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskContacts_Email",
                table: "ServiceDeskContacts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskMessages_MessageId",
                table: "ServiceDeskMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskMessages_TicketId",
                table: "ServiceDeskMessages",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskSettings_CustomAddress",
                table: "ServiceDeskSettings",
                column: "CustomAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskSettings_DefaultAssigneeId",
                table: "ServiceDeskSettings",
                column: "DefaultAssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ContactId",
                table: "ServiceDeskTickets",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_IssueId",
                table: "ServiceDeskTickets",
                column: "IssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ProjectId_LastRequesterActivityAt",
                table: "ServiceDeskTickets",
                columns: new[] { "ProjectId", "LastRequesterActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ReplyToken",
                table: "ServiceDeskTickets",
                column: "ReplyToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_TrackingToken",
                table: "ServiceDeskTickets",
                column: "TrackingToken",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_ServiceDeskContacts_ServiceDeskContactId",
                table: "Comments",
                column: "ServiceDeskContactId",
                principalTable: "ServiceDeskContacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

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
                name: "FK_Comments_ServiceDeskContacts_ServiceDeskContactId",
                table: "Comments");

            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_Deployments_RedeployOfId",
                table: "Deployments");

            migrationBuilder.DropTable(
                name: "ChatUserLinks");

            migrationBuilder.DropTable(
                name: "CiEnvironmentApprovers");

            migrationBuilder.DropTable(
                name: "ContainerBlobs");

            migrationBuilder.DropTable(
                name: "DeployFreezeWindows");

            migrationBuilder.DropTable(
                name: "DeploymentApprovals");

            migrationBuilder.DropTable(
                name: "MergeQueueEntries");

            migrationBuilder.DropTable(
                name: "PackageDistTags");

            migrationBuilder.DropTable(
                name: "PackageFiles");

            migrationBuilder.DropTable(
                name: "ProjectPackageSettings");

            migrationBuilder.DropTable(
                name: "RepositorySecuritySettings");

            migrationBuilder.DropTable(
                name: "SecurityFindings");

            migrationBuilder.DropTable(
                name: "SecurityTriages");

            migrationBuilder.DropTable(
                name: "ServiceDeskMessages");

            migrationBuilder.DropTable(
                name: "ServiceDeskSettings");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "SecurityScans");

            migrationBuilder.DropTable(
                name: "ServiceDeskTickets");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropTable(
                name: "ServiceDeskContacts");

            migrationBuilder.DropIndex(
                name: "IX_Deployments_CommitSha",
                table: "Deployments");

            migrationBuilder.DropIndex(
                name: "IX_Deployments_RedeployOfId",
                table: "Deployments");

            migrationBuilder.DropIndex(
                name: "IX_Comments_ServiceDeskContactId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ChatSettings",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "SlackDmEnabled",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "EnvironmentAction",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "ChatPending",
                table: "Notifications");

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
                name: "IsPublicReply",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ServiceDeskContactId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "RequiredApprovals",
                table: "CiEnvironments");

            migrationBuilder.DropColumn(
                name: "StoppedAt",
                table: "CiEnvironments");

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
