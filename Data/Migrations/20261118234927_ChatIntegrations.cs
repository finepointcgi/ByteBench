using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChatSettings",
                table: "Webhooks",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Webhooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Webhooks",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SlackDmEnabled",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ChatPending",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ChatUserLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TeamId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ExternalUserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nonce = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatUserLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatUserLinks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_Nonce",
                table: "ChatUserLinks",
                column: "Nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_Provider_TeamId_ExternalUserId",
                table: "ChatUserLinks",
                columns: new[] { "Provider", "TeamId", "ExternalUserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_UserId",
                table: "ChatUserLinks",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatUserLinks");

            migrationBuilder.DropColumn(
                name: "ChatSettings",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Webhooks");

            migrationBuilder.DropColumn(
                name: "SlackDmEnabled",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "ChatPending",
                table: "Notifications");
        }
    }
}
