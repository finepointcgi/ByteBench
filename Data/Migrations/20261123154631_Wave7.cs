using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Wave7 : Migration
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
                name: "DependencyBotPullRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ecosystem = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Package = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ToVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    PullRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyBotPullRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencyBotPullRequests_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DependencyBotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Schedule = table.Column<int>(type: "INTEGER", nullable: false),
                    Ecosystems = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    Grouped = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxOpenPullRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    IgnoreList = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    Labels = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Reviewers = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastReportJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyBotSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencyBotSettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateTable(
                name: "RepositoryInsights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CommitsScanned = table.Column<int>(type: "INTEGER", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryInsights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryInsights_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SamlMessages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ReturnUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SamlMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScimIdentities",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimIdentities", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_ScimIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommitSignatures_Sha",
                table: "CommitSignatures",
                column: "Sha",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotPullRequests_RepositoryId_Ecosystem_Package",
                table: "DependencyBotPullRequests",
                columns: new[] { "RepositoryId", "Ecosystem", "Package" });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotSettings_Enabled_NextRunAt",
                table: "DependencyBotSettings",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotSettings_RepositoryId",
                table: "DependencyBotSettings",
                column: "RepositoryId",
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

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryInsights_RepositoryId",
                table: "RepositoryInsights",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SamlMessages_ExpiresAt",
                table: "SamlMessages",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScimIdentities_ExternalId",
                table: "ScimIdentities",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimIdentities_NormalizedUserName",
                table: "ScimIdentities",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimTokens_TokenHash",
                table: "ScimTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommitSignatures");

            migrationBuilder.DropTable(
                name: "DependencyBotPullRequests");

            migrationBuilder.DropTable(
                name: "DependencyBotSettings");

            migrationBuilder.DropTable(
                name: "GpgKeys");

            migrationBuilder.DropTable(
                name: "PullRequestAutoMerges");

            migrationBuilder.DropTable(
                name: "RepositoryInsights");

            migrationBuilder.DropTable(
                name: "SamlMessages");

            migrationBuilder.DropTable(
                name: "ScimIdentities");

            migrationBuilder.DropTable(
                name: "ScimTokens");

            migrationBuilder.DropColumn(
                name: "RequireSignedCommits",
                table: "BranchProtectionRules");
        }
    }
}
