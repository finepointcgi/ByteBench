using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositorySecuritySettings");

            migrationBuilder.DropTable(
                name: "SecurityFindings");

            migrationBuilder.DropTable(
                name: "SecurityTriages");

            migrationBuilder.DropTable(
                name: "SecurityScans");
        }
    }
}
