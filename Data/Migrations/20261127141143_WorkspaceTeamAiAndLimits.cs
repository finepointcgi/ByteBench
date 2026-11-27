using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceTeamAiAndLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiChangeSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiChangeSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiChangeSets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedById = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StopReason = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BaseBranch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    Steps = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiRuns_AspNetUsers_RequestedById",
                        column: x => x.RequestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiRuns_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiRuns_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiWorkspacePreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiWorkspacePreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiWorkspacePreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiWorkspacePreferences_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryPrebuildSettings",
                columns: table => new
                {
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Branches = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    KeepLast = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryPrebuildSettings", x => x.RepositoryId);
                    table.ForeignKey(
                        name: "FK_RepositoryPrebuildSettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserWorkspacePreferences",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWorkspacePreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserWorkspacePreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceAllocations",
                columns: table => new
                {
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    PrebuildId = table.Column<int>(type: "INTEGER", nullable: true),
                    CpuMillis = table.Column<int>(type: "INTEGER", nullable: false),
                    MemoryMb = table.Column<int>(type: "INTEGER", nullable: false),
                    DiskMb = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceAllocations", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_WorkspaceAllocations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceEgressRecords",
                columns: table => new
                {
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AdminMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RepoNarrowed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceEgressRecords", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_WorkspaceEgressRecords_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceLinks_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceLinks_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceLinks_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspacePrebuilds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TriggeredById = table.Column<int>(type: "INTEGER", nullable: true),
                    ImageTag = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    BaseImage = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Log = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GitTokenHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                    SubmoduleRepoIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspacePrebuilds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspacePrebuilds_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceShares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvitedById = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceShares_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceShares_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiChangeFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChangeSetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalText = table.Column<string>(type: "TEXT", nullable: true),
                    NewText = table.Column<string>(type: "TEXT", nullable: true),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    AcceptedHunksJson = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiChangeFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiChangeFiles_AiChangeSets_ChangeSetId",
                        column: x => x.ChangeSetId,
                        principalTable: "AiChangeSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiRunEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiRunEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiRunEvents_AiRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AiRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiChangeFiles_ChangeSetId",
                table: "AiChangeFiles",
                column: "ChangeSetId");

            migrationBuilder.CreateIndex(
                name: "IX_AiChangeSets_WorkspaceId_State",
                table: "AiChangeSets",
                columns: new[] { "WorkspaceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AiRunEvents_RunId_Id",
                table: "AiRunEvents",
                columns: new[] { "RunId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AiRuns_IssueId_Status",
                table: "AiRuns",
                columns: new[] { "IssueId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiRuns_PublicId",
                table: "AiRuns",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiRuns_RepositoryId",
                table: "AiRuns",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AiRuns_RequestedById",
                table: "AiRuns",
                column: "RequestedById");

            migrationBuilder.CreateIndex(
                name: "IX_AiRuns_Status_CreatedAt",
                table: "AiRuns",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiWorkspacePreferences_UserId_WorkspaceId",
                table: "AiWorkspacePreferences",
                columns: new[] { "UserId", "WorkspaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiWorkspacePreferences_WorkspaceId",
                table: "AiWorkspacePreferences",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceLinks_IssueId",
                table: "WorkspaceLinks",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceLinks_PullRequestId",
                table: "WorkspaceLinks",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceLinks_WorkspaceId",
                table: "WorkspaceLinks",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspacePrebuilds_RepositoryId_Ref_State",
                table: "WorkspacePrebuilds",
                columns: new[] { "RepositoryId", "Ref", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceShares_UserId_EndedAt",
                table: "WorkspaceShares",
                columns: new[] { "UserId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceShares_WorkspaceId_UserId",
                table: "WorkspaceShares",
                columns: new[] { "WorkspaceId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiChangeFiles");

            migrationBuilder.DropTable(
                name: "AiRunEvents");

            migrationBuilder.DropTable(
                name: "AiWorkspacePreferences");

            migrationBuilder.DropTable(
                name: "RepositoryPrebuildSettings");

            migrationBuilder.DropTable(
                name: "UserWorkspacePreferences");

            migrationBuilder.DropTable(
                name: "WorkspaceAllocations");

            migrationBuilder.DropTable(
                name: "WorkspaceEgressRecords");

            migrationBuilder.DropTable(
                name: "WorkspaceLinks");

            migrationBuilder.DropTable(
                name: "WorkspacePrebuilds");

            migrationBuilder.DropTable(
                name: "WorkspaceShares");

            migrationBuilder.DropTable(
                name: "AiChangeSets");

            migrationBuilder.DropTable(
                name: "AiRuns");
        }
    }
}
