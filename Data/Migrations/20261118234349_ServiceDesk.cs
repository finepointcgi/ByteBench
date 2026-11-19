using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceDesk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublicReply",
                table: "Comments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ServiceDeskContactId",
                table: "Comments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceDeskContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicFormEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomAddress = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    DefaultIssueType = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultLabelIds = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultAssigneeId = table.Column<int>(type: "INTEGER", nullable: true),
                    IssueTemplate = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    AutoReplyEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoReplyTemplate = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    NotifyStatusIds = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseTargetHours = table.Column<int>(type: "INTEGER", nullable: false),
                    FormIntro = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ServiceDeskSettings_AspNetUsers_DefaultAssigneeId",
                        column: x => x.DefaultAssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ServiceDeskSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContactId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplyToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TrackingToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    InitialMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRequesterActivityAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstResponseAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAgentReplyAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_ServiceDeskContacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "ServiceDeskContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceDeskMessages_ServiceDeskTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "ServiceDeskTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ServiceDeskContactId",
                table: "Comments",
                column: "ServiceDeskContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskContacts_Email",
                table: "ServiceDeskContacts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskMessages_MessageId",
                table: "ServiceDeskMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskMessages_TicketId",
                table: "ServiceDeskMessages",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskSettings_CustomAddress",
                table: "ServiceDeskSettings",
                column: "CustomAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskSettings_DefaultAssigneeId",
                table: "ServiceDeskSettings",
                column: "DefaultAssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ContactId",
                table: "ServiceDeskTickets",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_IssueId",
                table: "ServiceDeskTickets",
                column: "IssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ProjectId_LastRequesterActivityAt",
                table: "ServiceDeskTickets",
                columns: new[] { "ProjectId", "LastRequesterActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ReplyToken",
                table: "ServiceDeskTickets",
                column: "ReplyToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_TrackingToken",
                table: "ServiceDeskTickets",
                column: "TrackingToken",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_ServiceDeskContacts_ServiceDeskContactId",
                table: "Comments",
                column: "ServiceDeskContactId",
                principalTable: "ServiceDeskContacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Comments_ServiceDeskContacts_ServiceDeskContactId",
                table: "Comments");

            migrationBuilder.DropTable(
                name: "ServiceDeskMessages");

            migrationBuilder.DropTable(
                name: "ServiceDeskSettings");

            migrationBuilder.DropTable(
                name: "ServiceDeskTickets");

            migrationBuilder.DropTable(
                name: "ServiceDeskContacts");

            migrationBuilder.DropIndex(
                name: "IX_Comments_ServiceDeskContactId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "IsPublicReply",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ServiceDeskContactId",
                table: "Comments");
        }
    }
}
