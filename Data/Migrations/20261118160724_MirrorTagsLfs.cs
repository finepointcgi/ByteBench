using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class MirrorTagsLfs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LfsObjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Oid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LfsObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LfsObjects_AspNetUsers_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LfsObjects_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedTagRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    CreateRole = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockDeletion = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedTagRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtectedTagRules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryMirrors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlyProtectedBranches = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    NextSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryMirrors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryMirrors_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RepositoryMirrors_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LfsObjects_Oid",
                table: "LfsObjects",
                column: "Oid");

            migrationBuilder.CreateIndex(
                name: "IX_LfsObjects_RepositoryId_Oid",
                table: "LfsObjects",
                columns: new[] { "RepositoryId", "Oid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LfsObjects_UploadedById",
                table: "LfsObjects",
                column: "UploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProtectedTagRules_RepositoryId_Pattern",
                table: "ProtectedTagRules",
                columns: new[] { "RepositoryId", "Pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_CreatedById",
                table: "RepositoryMirrors",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_Direction_Enabled_NextSyncAt",
                table: "RepositoryMirrors",
                columns: new[] { "Direction", "Enabled", "NextSyncAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_OnePullPerRepository",
                table: "RepositoryMirrors",
                column: "RepositoryId",
                unique: true,
                filter: "\"Direction\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryMirrors_RepositoryId",
                table: "RepositoryMirrors",
                column: "RepositoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LfsObjects");

            migrationBuilder.DropTable(
                name: "ProtectedTagRules");

            migrationBuilder.DropTable(
                name: "RepositoryMirrors");
        }
    }
}
