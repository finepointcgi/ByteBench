using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class DependencyPrivateRegistries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DependencyBotRegistries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ecosystem = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AuthKind = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedCredential = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    EncryptedUsername = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ExtraOrigins = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyBotRegistries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencyBotRegistries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyBotRegistries_RepositoryId_Ecosystem_Scope",
                table: "DependencyBotRegistries",
                columns: new[] { "RepositoryId", "Ecosystem", "Scope" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DependencyBotRegistries");
        }
    }
}
