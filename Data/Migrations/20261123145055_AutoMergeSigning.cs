using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoMergeSigning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireSignedCommits",
                table: "BranchProtectionRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CommitSignatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    SignerUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    KeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    KeysStamp = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommitSignatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GpgKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    KeyFingerprints = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Emails = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpgKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GpgKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PullRequestAutoMerges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    ArmedById = table.Column<int>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    DeleteSourceBranch = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArmedHeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ArmedTargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ArmedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WaitingFor = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestAutoMerges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestAutoMerges_AspNetUsers_ArmedById",
                        column: x => x.ArmedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequestAutoMerges_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommitSignatures_Sha",
                table: "CommitSignatures",
                column: "Sha",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpgKeys_Fingerprint",
                table: "GpgKeys",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpgKeys_UserId",
                table: "GpgKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestAutoMerges_ArmedById",
                table: "PullRequestAutoMerges",
                column: "ArmedById");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestAutoMerges_PullRequestId",
                table: "PullRequestAutoMerges",
                column: "PullRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestAutoMerges_State",
                table: "PullRequestAutoMerges",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommitSignatures");

            migrationBuilder.DropTable(
                name: "GpgKeys");

            migrationBuilder.DropTable(
                name: "PullRequestAutoMerges");

            migrationBuilder.DropColumn(
                name: "RequireSignedCommits",
                table: "BranchProtectionRules");
        }
    }
}
