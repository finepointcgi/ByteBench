using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackerImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LocalId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackerImportJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedById = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SourceProject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IssuesExpected = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuesRead = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuesCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuesSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    CommentsCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    LabelsCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    MilestonesCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    LinksCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackerImportJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackerImportJobs_AspNetUsers_RequestedById",
                        column: x => x.RequestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TrackerImportJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedItems_ProjectId_SourceKey_Kind_ExternalId",
                table: "ImportedItems",
                columns: new[] { "ProjectId", "SourceKey", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackerImportJobs_ProjectId_Status",
                table: "TrackerImportJobs",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackerImportJobs_RequestedById",
                table: "TrackerImportJobs",
                column: "RequestedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedItems");

            migrationBuilder.DropTable(
                name: "TrackerImportJobs");
        }
    }
}
