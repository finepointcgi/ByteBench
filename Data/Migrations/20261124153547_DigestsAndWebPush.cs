using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class DigestsAndWebPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestFailures",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DigestFrequency",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DigestHour",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DigestWeekday",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDigestAt",
                table: "UserNotificationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextDigestAt",
                table: "UserNotificationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushEnabled",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PushGroups",
                table: "UserNotificationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "UserNotificationSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushPending",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DigestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Preview = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnreadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MentionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DueCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MyPullRequestCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigestLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DigestLogs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebPushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    EndpointHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EncryptedP256dh = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    EncryptedAuth = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryAfterUntil = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPushSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationSettings_NextDigestAt",
                table: "UserNotificationSettings",
                column: "NextDigestAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_PushPending",
                table: "Notifications",
                column: "PushPending");

            migrationBuilder.CreateIndex(
                name: "IX_DigestLogs_CreatedAt",
                table: "DigestLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DigestLogs_UserId",
                table: "DigestLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPushSubscriptions_EndpointHash",
                table: "WebPushSubscriptions",
                column: "EndpointHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPushSubscriptions_UserId",
                table: "WebPushSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DigestLogs");

            migrationBuilder.DropTable(
                name: "WebPushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_UserNotificationSettings_NextDigestAt",
                table: "UserNotificationSettings");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_PushPending",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DigestFailures",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "DigestFrequency",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "DigestHour",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "DigestWeekday",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "LastDigestAt",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "NextDigestAt",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "PushEnabled",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "PushGroups",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "UserNotificationSettings");

            migrationBuilder.DropColumn(
                name: "PushPending",
                table: "Notifications");
        }
    }
}
