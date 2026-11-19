using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiPullRequestReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PullRequestComments_PullRequestId",
                table: "PullRequestComments");

            migrationBuilder.AddColumn<bool>(
                name: "AiDismissed",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiFingerprint",
                table: "PullRequestComments",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiReviewId",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiSeverity",
                table: "PullRequestComments",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Backend",
                table: "AiReviews",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiffChars",
                table: "AiReviews",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RepositoryAiReviewSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxDiffChars = table.Column<int>(type: "INTEGER", nullable: false),
                    IgnorePaths = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    FocusPrompt = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    BlockOnUnresolvedFindings = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedById = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryAiReviewSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryAiReviewSettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_PullRequestId_AiFingerprint",
                table: "PullRequestComments",
                columns: new[] { "PullRequestId", "AiFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryAiReviewSettings_RepositoryId",
                table: "RepositoryAiReviewSettings",
                column: "RepositoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryAiReviewSettings");

            migrationBuilder.DropIndex(
                name: "IX_PullRequestComments_PullRequestId_AiFingerprint",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiDismissed",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiFingerprint",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiReviewId",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AiSeverity",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "Backend",
                table: "AiReviews");

            migrationBuilder.DropColumn(
                name: "DiffChars",
                table: "AiReviews");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_PullRequestId",
                table: "PullRequestComments",
                column: "PullRequestId");
        }
    }
}
