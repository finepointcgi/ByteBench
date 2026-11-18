using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Packages_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectPackageSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeepLatest = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectPackageSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectPackageSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContainerBlobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    IsManifest = table.Column<bool>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerBlobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContainerBlobs_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageDistTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageDistTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageDistTags_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NormalizedVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestDigest = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedById = table.Column<int>(type: "INTEGER", nullable: true),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageVersions_AspNetUsers_PublishedById",
                        column: x => x.PublishedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PackageVersions_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sha1 = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Sha512 = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageFiles_PackageVersions_PackageVersionId",
                        column: x => x.PackageVersionId,
                        principalTable: "PackageVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerBlobs_PackageId_Sha256",
                table: "ContainerBlobs",
                columns: new[] { "PackageId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContainerBlobs_Sha256",
                table: "ContainerBlobs",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_PackageDistTags_PackageId_Tag",
                table: "PackageDistTags",
                columns: new[] { "PackageId", "Tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageFiles_PackageVersionId",
                table: "PackageFiles",
                column: "PackageVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageFiles_Sha256",
                table: "PackageFiles",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ProjectId_Type_NormalizedName",
                table: "Packages",
                columns: new[] { "ProjectId", "Type", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PackageId_NormalizedVersion",
                table: "PackageVersions",
                columns: new[] { "PackageId", "NormalizedVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_PublishedById",
                table: "PackageVersions",
                column: "PublishedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerBlobs");

            migrationBuilder.DropTable(
                name: "PackageDistTags");

            migrationBuilder.DropTable(
                name: "PackageFiles");

            migrationBuilder.DropTable(
                name: "ProjectPackageSettings");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "Packages");
        }
    }
}
