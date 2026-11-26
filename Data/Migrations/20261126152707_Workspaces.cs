using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Workspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectWorkspaceSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWorkspaceSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectWorkspaceSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Image = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PortMapJson = table.Column<string>(type: "TEXT", nullable: false),
                    EgressMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CanPush = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConnectionTokenHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    GitTokenHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StoppedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StopReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workspaces_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Workspaces_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_OwnerId_State",
                table: "Workspaces",
                columns: new[] { "OwnerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_PublicId",
                table: "Workspaces",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_RepositoryId",
                table: "Workspaces",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_State",
                table: "Workspaces",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectWorkspaceSettings");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
