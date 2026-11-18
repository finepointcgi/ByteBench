using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Waves23 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ForkedFromId",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCrossFork",
                table: "PullRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SourceRepositoryId",
                table: "PullRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoursPerDay",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<bool>(
                name: "WikiEnabled",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "Pipelines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedById",
                table: "Pipelines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AwaitingApproval",
                table: "Pipelines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsForkPullRequest",
                table: "Pipelines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleId",
                table: "Pipelines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariablesJson",
                table: "Pipelines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowFailure",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentName",
                table: "PipelineJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentUrl",
                table: "PipelineJobs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtrasJson",
                table: "PipelineJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManual",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlayedAt",
                table: "PipelineJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayedById",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OriginalEstimateMinutes",
                table: "Issues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemainingEstimateMinutes",
                table: "Issues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "Issues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdminGrantedBy",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    Definition = table.Column<string>(type: "TEXT", nullable: false),
                    RunAs = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowRuleTriggers = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScheduledRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RoundRobinLastUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                    table.CheckConstraint("CK_AutomationRules_Scope", "(\"ProjectId\" IS NULL) <> (\"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_AutomationRules_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    FollowsWorkflow = table.Column<bool>(type: "INTEGER", nullable: false),
                    FilterQuery = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FilterLabelIds = table.Column<string>(type: "TEXT", nullable: false),
                    FilterAssigneeId = table.Column<int>(type: "INTEGER", nullable: true),
                    FilterType = table.Column<int>(type: "INTEGER", nullable: true),
                    FilterEpicId = table.Column<int>(type: "INTEGER", nullable: true),
                    Swimlane = table.Column<int>(type: "INTEGER", nullable: false),
                    CardFields = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockOverWip = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoneDays = table.Column<int>(type: "INTEGER", nullable: true),
                    StartOnActiveSprint = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Boards_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CiCacheEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiCacheEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiCacheEntries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CiEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiEnvironments_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Options = table.Column<string>(type: "TEXT", nullable: false),
                    IssueTypes = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFields_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dashboards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SharedProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedGroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dashboards_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dashboards_Groups_SharedGroupId",
                        column: x => x.SharedGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Dashboards_Projects_SharedProjectId",
                        column: x => x.SharedProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    UsersChecked = table.Column<int>(type: "INTEGER", nullable: false),
                    UsersUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    UsersDeactivated = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalGroupMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalGroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalGroupMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalGroupMemberships_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    TitlePrefix = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    LabelIds = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTemplates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LfsObjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Oid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LfsObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LfsObjects_AspNetUsers_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LfsObjects_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Keep = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineArtifacts_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PipelineArtifacts_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    VariablesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineSchedules_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PipelineSchedules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LeadId = table.Column<int>(type: "INTEGER", nullable: true),
                    DefaultAssigneeId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectComponents_AspNetUsers_DefaultAssigneeId",
                        column: x => x.DefaultAssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProjectComponents_AspNetUsers_LeadId",
                        column: x => x.LeadId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProjectComponents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsReleased = table.Column<bool>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectVersions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedTagRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CreateRole = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockDeletion = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedTagRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtectedTagRules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryMirrors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlyProtectedBranches = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    NextSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryMirrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryMirrors_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RepositoryMirrors_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Snippets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snippets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Snippets_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Snippets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiPageIndex",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPageIndex", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPageIndex_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkLogs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkLogs_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: true),
                    IssueKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Actions = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuns_AutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BoardColumns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    WipLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    StatusIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardColumns_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BoardQuickFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardQuickFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardQuickFilters_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deployments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Deployments_CiEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "CiEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deployments_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deployments_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    TextValue = table.Column<string>(type: "TEXT", nullable: true),
                    NumberValue = table.Column<double>(type: "REAL", nullable: true),
                    DateValue = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    BoolValue = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_CustomFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "CustomFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DashboardGadgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DashboardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Config = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardGadgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardGadgets_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DashboardHomes",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DashboardId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardHomes", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_DashboardHomes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DashboardHomes_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueComponents",
                columns: table => new
                {
                    ComponentsId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuesId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueComponents", x => new { x.ComponentsId, x.IssuesId });
                    table.ForeignKey(
                        name: "FK_IssueComponents_Issues_IssuesId",
                        column: x => x.IssuesId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueComponents_ProjectComponents_ComponentsId",
                        column: x => x.ComponentsId,
                        principalTable: "ProjectComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueVersions",
                columns: table => new
                {
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    VersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueVersions", x => new { x.IssueId, x.VersionId, x.Kind });
                    table.ForeignKey(
                        name: "FK_IssueVersions_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueVersions_ProjectVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ProjectVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnippetComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnippetId = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnippetComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnippetComments_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SnippetComments_Snippets_SnippetId",
                        column: x => x.SnippetId,
                        principalTable: "Snippets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnippetRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnippetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnippetRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnippetRevisions_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SnippetRevisions_Snippets_SnippetId",
                        column: x => x.SnippetId,
                        principalTable: "Snippets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnippetFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RevisionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnippetFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnippetFiles_SnippetRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "SnippetRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ForkedFromId",
                table: "Repositories",
                column: "ForkedFromId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_SourceRepositoryId_State",
                table: "PullRequests",
                columns: new[] { "SourceRepositoryId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_ApprovedById",
                table: "Pipelines",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_ScheduleId",
                table: "Pipelines",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineJobs_PlayedById",
                table: "PipelineJobs",
                column: "PlayedById");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_AuthorId",
                table: "AutomationRules",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_GroupId",
                table: "AutomationRules",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_ProjectId",
                table: "AutomationRules",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_RuleId_StartedAt",
                table: "AutomationRuns",
                columns: new[] { "RuleId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BoardColumns_BoardId",
                table: "BoardColumns",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardQuickFilters_BoardId",
                table: "BoardQuickFilters",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Boards_OneDefaultPerProject",
                table: "Boards",
                column: "ProjectId",
                unique: true,
                filter: "\"IsDefault\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CiCacheEntries_RepositoryId_IsProtected_Key",
                table: "CiCacheEntries",
                columns: new[] { "RepositoryId", "IsProtected", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CiCacheEntries_StoredName",
                table: "CiCacheEntries",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironments_RepositoryId_Name",
                table: "CiEnvironments",
                columns: new[] { "RepositoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFields_ProjectId_Key",
                table: "CustomFields",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_FieldId_TextValue",
                table: "CustomFieldValues",
                columns: new[] { "FieldId", "TextValue" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_IssueId_FieldId",
                table: "CustomFieldValues",
                columns: new[] { "IssueId", "FieldId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_UserId",
                table: "CustomFieldValues",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardGadgets_DashboardId_Order",
                table: "DashboardGadgets",
                columns: new[] { "DashboardId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardHomes_DashboardId",
                table: "DashboardHomes",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_OwnerId",
                table: "Dashboards",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_SharedGroupId",
                table: "Dashboards",
                column: "SharedGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_SharedProjectId",
                table: "Dashboards",
                column: "SharedProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_EnvironmentId_Id",
                table: "Deployments",
                columns: new[] { "EnvironmentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_JobId",
                table: "Deployments",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_PipelineId",
                table: "Deployments",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_UserId",
                table: "Deployments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncRuns_Source_StartedAt",
                table: "DirectorySyncRuns",
                columns: new[] { "Source", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMemberships_GroupId",
                table: "ExternalGroupMemberships",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMemberships_UserId_GroupId_Source",
                table: "ExternalGroupMemberships",
                columns: new[] { "UserId", "GroupId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueComponents_IssuesId",
                table: "IssueComponents",
                column: "IssuesId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTemplates_ProjectId",
                table: "IssueTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueVersions_VersionId_Kind",
                table: "IssueVersions",
                columns: new[] { "VersionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_LfsObjects_Oid",
                table: "LfsObjects",
                column: "Oid");

            migrationBuilder.CreateIndex(
                name: "IX_LfsObjects_RepositoryId_Oid",
                table: "LfsObjects",
                columns: new[] { "RepositoryId", "Oid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LfsObjects_UploadedById",
                table: "LfsObjects",
                column: "UploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_ExpiresAt",
                table: "PipelineArtifacts",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_JobId",
                table: "PipelineArtifacts",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_PipelineId",
                table: "PipelineArtifacts",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_RepositoryId",
                table: "PipelineArtifacts",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_StoredName",
                table: "PipelineArtifacts",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSchedules_Active_NextRunAt",
                table: "PipelineSchedules",
                columns: new[] { "Active", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSchedules_OwnerId",
                table: "PipelineSchedules",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSchedules_RepositoryId",
                table: "PipelineSchedules",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectComponents_DefaultAssigneeId",
                table: "ProjectComponents",
                column: "DefaultAssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectComponents_LeadId",
                table: "ProjectComponents",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectComponents_ProjectId_Name",
                table: "ProjectComponents",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersions_ProjectId_Name",
                table: "ProjectVersions",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProtectedTagRules_RepositoryId_Pattern",
                table: "ProtectedTagRules",
                columns: new[] { "RepositoryId", "Pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_CreatedById",
                table: "RepositoryMirrors",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_Direction_Enabled_NextSyncAt",
                table: "RepositoryMirrors",
                columns: new[] { "Direction", "Enabled", "NextSyncAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_OnePullPerRepository",
                table: "RepositoryMirrors",
                column: "RepositoryId",
                unique: true,
                filter: "\"Direction\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_RepositoryId",
                table: "RepositoryMirrors",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SnippetComments_AuthorId",
                table: "SnippetComments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SnippetComments_SnippetId_CreatedAt",
                table: "SnippetComments",
                columns: new[] { "SnippetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SnippetFiles_RevisionId_Position",
                table: "SnippetFiles",
                columns: new[] { "RevisionId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_SnippetRevisions_AuthorId",
                table: "SnippetRevisions",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SnippetRevisions_SnippetId_Number",
                table: "SnippetRevisions",
                columns: new[] { "SnippetId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Snippets_AuthorId_UpdatedAt",
                table: "Snippets",
                columns: new[] { "AuthorId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Snippets_ProjectId_UpdatedAt",
                table: "Snippets",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPageIndex_ProjectId_Path",
                table: "WikiPageIndex",
                columns: new[] { "ProjectId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_IssueId_Date",
                table: "WorkLogs",
                columns: new[] { "IssueId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_UserId_Date",
                table: "WorkLogs",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.AddForeignKey(
                name: "FK_PipelineJobs_AspNetUsers_PlayedById",
                table: "PipelineJobs",
                column: "PlayedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Pipelines_AspNetUsers_ApprovedById",
                table: "Pipelines",
                column: "ApprovedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Pipelines_PipelineSchedules_ScheduleId",
                table: "Pipelines",
                column: "ScheduleId",
                principalTable: "PipelineSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PullRequests_Repositories_SourceRepositoryId",
                table: "PullRequests",
                column: "SourceRepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Repositories_Repositories_ForkedFromId",
                table: "Repositories",
                column: "ForkedFromId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PipelineJobs_AspNetUsers_PlayedById",
                table: "PipelineJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_Pipelines_AspNetUsers_ApprovedById",
                table: "Pipelines");

            migrationBuilder.DropForeignKey(
                name: "FK_Pipelines_PipelineSchedules_ScheduleId",
                table: "Pipelines");

            migrationBuilder.DropForeignKey(
                name: "FK_PullRequests_Repositories_SourceRepositoryId",
                table: "PullRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Repositories_Repositories_ForkedFromId",
                table: "Repositories");

            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropTable(
                name: "BoardColumns");

            migrationBuilder.DropTable(
                name: "BoardQuickFilters");

            migrationBuilder.DropTable(
                name: "CiCacheEntries");

            migrationBuilder.DropTable(
                name: "CustomFieldValues");

            migrationBuilder.DropTable(
                name: "DashboardGadgets");

            migrationBuilder.DropTable(
                name: "DashboardHomes");

            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "DirectorySyncRuns");

            migrationBuilder.DropTable(
                name: "ExternalGroupMemberships");

            migrationBuilder.DropTable(
                name: "IssueComponents");

            migrationBuilder.DropTable(
                name: "IssueTemplates");

            migrationBuilder.DropTable(
                name: "IssueVersions");

            migrationBuilder.DropTable(
                name: "LfsObjects");

            migrationBuilder.DropTable(
                name: "PipelineArtifacts");

            migrationBuilder.DropTable(
                name: "PipelineSchedules");

            migrationBuilder.DropTable(
                name: "ProtectedTagRules");

            migrationBuilder.DropTable(
                name: "RepositoryMirrors");

            migrationBuilder.DropTable(
                name: "SnippetComments");

            migrationBuilder.DropTable(
                name: "SnippetFiles");

            migrationBuilder.DropTable(
                name: "WikiPageIndex");

            migrationBuilder.DropTable(
                name: "WorkLogs");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropTable(
                name: "Boards");

            migrationBuilder.DropTable(
                name: "CustomFields");

            migrationBuilder.DropTable(
                name: "Dashboards");

            migrationBuilder.DropTable(
                name: "CiEnvironments");

            migrationBuilder.DropTable(
                name: "ProjectComponents");

            migrationBuilder.DropTable(
                name: "ProjectVersions");

            migrationBuilder.DropTable(
                name: "SnippetRevisions");

            migrationBuilder.DropTable(
                name: "Snippets");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_ForkedFromId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_PullRequests_SourceRepositoryId_State",
                table: "PullRequests");

            migrationBuilder.DropIndex(
                name: "IX_Pipelines_ApprovedById",
                table: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_Pipelines_ScheduleId",
                table: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_PipelineJobs_PlayedById",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "ForkedFromId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "IsCrossFork",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "SourceRepositoryId",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "HoursPerDay",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WikiEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ApprovedById",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "AwaitingApproval",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "IsForkPullRequest",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "VariablesJson",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "AllowFailure",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "EnvironmentName",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "EnvironmentUrl",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "ExtrasJson",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "IsManual",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "PlayedAt",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "PlayedById",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "OriginalEstimateMinutes",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "RemainingEstimateMinutes",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "AdminGrantedBy",
                table: "AspNetUsers");
        }
    }
}
