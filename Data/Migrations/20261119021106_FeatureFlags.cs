using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureFlags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OffValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IssueNumbers = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureFlags_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FeatureFlags_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagEnvironments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    MembersJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagSegments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    FlagId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    EnvironmentName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ActorId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagChanges_FeatureFlags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagEnvironmentStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FlagId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    LastEvaluatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagEnvironmentStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_FeatureFlags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_FlagEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "FlagEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagSdkKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    RotatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagSdkKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagSdkKeys_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlagSdkKeys_FlagEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "FlagEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_CreatedById",
                table: "FeatureFlags",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_ProjectId_Key",
                table: "FeatureFlags",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagChanges_FlagId_At",
                table: "FlagChanges",
                columns: new[] { "FlagId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironments_ProjectId_Name",
                table: "FlagEnvironments",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_EnvironmentId",
                table: "FlagEnvironmentStates",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_FlagId_EnvironmentId",
                table: "FlagEnvironmentStates",
                columns: new[] { "FlagId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_UpdatedById",
                table: "FlagEnvironmentStates",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_CreatedById",
                table: "FlagSdkKeys",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_EnvironmentId",
                table: "FlagSdkKeys",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_KeyHash",
                table: "FlagSdkKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagSegments_ProjectId_Key",
                table: "FlagSegments",
                columns: new[] { "ProjectId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlagChanges");

            migrationBuilder.DropTable(
                name: "FlagEnvironmentStates");

            migrationBuilder.DropTable(
                name: "FlagSdkKeys");

            migrationBuilder.DropTable(
                name: "FlagSegments");

            migrationBuilder.DropTable(
                name: "FeatureFlags");

            migrationBuilder.DropTable(
                name: "FlagEnvironments");
        }
    }
}
