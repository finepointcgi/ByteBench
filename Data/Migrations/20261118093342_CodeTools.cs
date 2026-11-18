using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RepositoryRedirects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    OldSlug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryRedirects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryRedirects_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRedirects_ProjectId_OldSlug",
                table: "RepositoryRedirects",
                columns: new[] { "ProjectId", "OldSlug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRedirects_RepositoryId",
                table: "RepositoryRedirects",
                column: "RepositoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryRedirects");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Repositories");
        }
    }
}
