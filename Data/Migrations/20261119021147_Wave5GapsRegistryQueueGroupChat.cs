using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Wave5GapsRegistryQueueGroupChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ProjectId",
                table: "Webhooks",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Webhooks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AwaitingApproval",
                table: "MergeQueueEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AwaitingApprovalSince",
                table: "MergeQueueEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeleteSourceBranch",
                table: "MergeQueueEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MergeQueueApprovalTimeoutMinutes",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ChatIntegrationOptOuts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    WebhookId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatIntegrationOptOuts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatIntegrationOptOuts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatIntegrationOptOuts_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_GroupId",
                table: "Webhooks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatIntegrationOptOuts_ProjectId_WebhookId",
                table: "ChatIntegrationOptOuts",
                columns: new[] { "ProjectId", "WebhookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatIntegrationOptOuts_WebhookId",
                table: "ChatIntegrationOptOuts",
                column: "WebhookId");

            migrationBuilder.AddForeignKey(
                name: "FK_Webhooks_Groups_GroupId",
                table: "Webhooks",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Webhooks_Groups_GroupId",
                table: "Webhooks");

            migrationBuilder.DropTable(
                name: "ChatIntegrationOptOuts");

            migrationBuilder.DropIndex(
                name: "IX_Webhooks_GroupId",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "AwaitingApproval",
                table: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "AwaitingApprovalSince",
                table: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "DeleteSourceBranch",
                table: "MergeQueueEntries");

            migrationBuilder.DropColumn(
                name: "MergeQueueApprovalTimeoutMinutes",
                table: "BranchProtectionRules");

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId",
                table: "Webhooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
