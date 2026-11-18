using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class EditorWorkbench : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkLogs");

            migrationBuilder.AddColumn<string>(
                name: "WorkbenchProjectCode",
                table: "Projects",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkbenchWbs",
                table: "Projects",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkbenchWbs",
                table: "Issues",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EditorWorkspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditorWorkspaces_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EditorWorkspaces_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectEditorSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    AiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectEditorSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectEditorSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EditorWorkspaceChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorWorkspaceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditorWorkspaceChanges_EditorWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "EditorWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EditorWorkspaceChanges_WorkspaceId_Path",
                table: "EditorWorkspaceChanges",
                columns: new[] { "WorkspaceId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EditorWorkspaces_RepositoryId",
                table: "EditorWorkspaces",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_EditorWorkspaces_UserId_RepositoryId_Branch",
                table: "EditorWorkspaces",
                columns: new[] { "UserId", "RepositoryId", "Branch" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EditorWorkspaceChanges");

            migrationBuilder.DropTable(
                name: "ProjectEditorSettings");

            migrationBuilder.DropTable(
                name: "EditorWorkspaces");

            migrationBuilder.DropColumn(
                name: "WorkbenchProjectCode",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkbenchWbs",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkbenchWbs",
                table: "Issues");

            migrationBuilder.CreateTable(
                name: "WorkLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Minutes = table.Column<int>(type: "INTEGER", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_IssueId_Date",
                table: "WorkLogs",
                columns: new[] { "IssueId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_UserId_Date",
                table: "WorkLogs",
                columns: new[] { "UserId", "Date" });
        }
    }
}
