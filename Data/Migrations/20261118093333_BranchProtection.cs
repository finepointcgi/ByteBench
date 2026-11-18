using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class BranchProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommitSha",
                table: "PullRequestReviews",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BranchProtectionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    RequiredApprovals = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockDirectPushes = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockForcePushes = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockDeletion = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireChecksToPass = table.Column<bool>(type: "INTEGER", nullable: false),
                    DismissStaleApprovals = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowAdminBypass = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultReviewerIds = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchProtectionRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchProtectionRules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PullRequestReviewers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestReviewers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_AspNetUsers_AddedById",
                        column: x => x.AddedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchProtectionRules_RepositoryId_Pattern",
                table: "BranchProtectionRules",
                columns: new[] { "RepositoryId", "Pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_AddedById",
                table: "PullRequestReviewers",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_PullRequestId_UserId",
                table: "PullRequestReviewers",
                columns: new[] { "PullRequestId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_UserId",
                table: "PullRequestReviewers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchProtectionRules");

            migrationBuilder.DropTable(
                name: "PullRequestReviewers");

            migrationBuilder.DropColumn(
                name: "CommitSha",
                table: "PullRequestReviews");
        }
    }
}
