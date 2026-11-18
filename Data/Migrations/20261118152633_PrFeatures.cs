using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllowedMergeStrategies",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultMergeStrategy",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDraft",
                table: "PullRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MergeStrategy",
                table: "PullRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliedCommitSha",
                table: "PullRequestComments",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommitSha",
                table: "PullRequestComments",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsResolved",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LineText",
                table: "PullRequestComments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentId",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "PullRequestComments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolvedById",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartLine",
                table: "PullRequestComments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireCodeOwnerApproval",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireThreadResolution",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_ParentId",
                table: "PullRequestComments",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_ResolvedById",
                table: "PullRequestComments",
                column: "ResolvedById");

            migrationBuilder.AddForeignKey(
                name: "FK_PullRequestComments_AspNetUsers_ResolvedById",
                table: "PullRequestComments",
                column: "ResolvedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PullRequestComments_PullRequestComments_ParentId",
                table: "PullRequestComments",
                column: "ParentId",
                principalTable: "PullRequestComments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PullRequestComments_AspNetUsers_ResolvedById",
                table: "PullRequestComments");

            migrationBuilder.DropForeignKey(
                name: "FK_PullRequestComments_PullRequestComments_ParentId",
                table: "PullRequestComments");

            migrationBuilder.DropIndex(
                name: "IX_PullRequestComments_ParentId",
                table: "PullRequestComments");

            migrationBuilder.DropIndex(
                name: "IX_PullRequestComments_ResolvedById",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "AllowedMergeStrategies",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "DefaultMergeStrategy",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "IsDraft",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "MergeStrategy",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "AppliedCommitSha",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "CommitSha",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "IsResolved",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "LineText",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "ResolvedById",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "StartLine",
                table: "PullRequestComments");

            migrationBuilder.DropColumn(
                name: "RequireCodeOwnerApproval",
                table: "BranchProtectionRules");

            migrationBuilder.DropColumn(
                name: "RequireThreadResolution",
                table: "BranchProtectionRules");
        }
    }
}
