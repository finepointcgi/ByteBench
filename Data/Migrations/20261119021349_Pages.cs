using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Pages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PagesDeployments");

            migrationBuilder.DropTable(
                name: "PagesSites");
        }
    }
}
