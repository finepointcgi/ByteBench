using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class InsightsAndDependencyBot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DependencyBotPullRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ecosystem = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Package = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ToVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyBotPullRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencyBotPullRequests_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DependencyBotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Schedule = table.Column<int>(type: "INTEGER", nullable: false),
                    Ecosystems = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    Grouped = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxOpenPullRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    IgnoreList = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    Labels = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Reviewers = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastReportJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyBotSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencyBotSettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryInsights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CommitsScanned = table.Column<int>(type: "INTEGER", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryInsights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryInsights_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotPullRequests_RepositoryId_Ecosystem_Package",
                table: "DependencyBotPullRequests",
                columns: new[] { "RepositoryId", "Ecosystem", "Package" });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotSettings_Enabled_NextRunAt",
                table: "DependencyBotSettings",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotSettings_RepositoryId",
                table: "DependencyBotSettings",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryInsights_RepositoryId",
                table: "RepositoryInsights",
                column: "RepositoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DependencyBotPullRequests");

            migrationBuilder.DropTable(
                name: "DependencyBotSettings");

            migrationBuilder.DropTable(
                name: "RepositoryInsights");
        }
    }
}
