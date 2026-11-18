using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Forks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ForkedFromId",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCrossFork",
                table: "PullRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SourceRepositoryId",
                table: "PullRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "Pipelines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedById",
                table: "Pipelines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AwaitingApproval",
                table: "Pipelines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsForkPullRequest",
                table: "Pipelines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ForkedFromId",
                table: "Repositories",
                column: "ForkedFromId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_SourceRepositoryId_State",
                table: "PullRequests",
                columns: new[] { "SourceRepositoryId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_ApprovedById",
                table: "Pipelines",
                column: "ApprovedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Pipelines_AspNetUsers_ApprovedById",
                table: "Pipelines",
                column: "ApprovedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PullRequests_Repositories_SourceRepositoryId",
                table: "PullRequests",
                column: "SourceRepositoryId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Repositories_Repositories_ForkedFromId",
                table: "Repositories",
                column: "ForkedFromId",
                principalTable: "Repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Pipelines_AspNetUsers_ApprovedById",
                table: "Pipelines");

            migrationBuilder.DropForeignKey(
                name: "FK_PullRequests_Repositories_SourceRepositoryId",
                table: "PullRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Repositories_Repositories_ForkedFromId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_ForkedFromId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_PullRequests_SourceRepositoryId_State",
                table: "PullRequests");

            migrationBuilder.DropIndex(
                name: "IX_Pipelines_ApprovedById",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ForkedFromId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "IsCrossFork",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "SourceRepositoryId",
                table: "PullRequests");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "ApprovedById",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "AwaitingApproval",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "IsForkPullRequest",
                table: "Pipelines");
        }
    }
}
