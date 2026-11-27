using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ByteBench.Data.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class WorkspacePrebuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryPrebuildSettings",
                columns: table => new
                {
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Branches = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    KeepLast = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    WorkspaceId = table.Column<int>(type: "integer", nullable: false),
                    PrebuildId = table.Column<int>(type: "integer", nullable: true),
                    CpuMillis = table.Column<int>(type: "integer", nullable: false),
                    MemoryMb = table.Column<int>(type: "integer", nullable: false),
                    DiskMb = table.Column<int>(type: "integer", nullable: false)
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
                name: "WorkspacePrebuilds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TriggeredById = table.Column<int>(type: "integer", nullable: true),
                    ImageTag = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BaseImage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Log = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GitTokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_WorkspacePrebuilds_RepositoryId_Ref_State",
                table: "WorkspacePrebuilds",
                columns: new[] { "RepositoryId", "Ref", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryPrebuildSettings");

            migrationBuilder.DropTable(
                name: "UserWorkspacePreferences");

            migrationBuilder.DropTable(
                name: "WorkspaceAllocations");

            migrationBuilder.DropTable(
                name: "WorkspacePrebuilds");
        }
    }
}
