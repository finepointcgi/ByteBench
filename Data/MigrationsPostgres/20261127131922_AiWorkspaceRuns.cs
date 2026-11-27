using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ByteBench.Data.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class AiWorkspaceRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiChangeSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkspaceId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    RequestedById = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StopReason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BaseBranch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkspaceId = table.Column<int>(type: "integer", nullable: true),
                    PullRequestId = table.Column<int>(type: "integer", nullable: true),
                    Steps = table.Column<int>(type: "integer", nullable: false),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    WorkspaceId = table.Column<int>(type: "integer", nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "AiChangeFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChangeSetId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OriginalText = table.Column<string>(type: "text", nullable: true),
                    NewText = table.Column<string>(type: "text", nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    AcceptedHunksJson = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
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
                name: "AiChangeSets");

            migrationBuilder.DropTable(
                name: "AiRuns");
        }
    }
}
