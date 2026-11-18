using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class Boards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    FollowsWorkflow = table.Column<bool>(type: "INTEGER", nullable: false),
                    FilterQuery = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FilterLabelIds = table.Column<string>(type: "TEXT", nullable: false),
                    FilterAssigneeId = table.Column<int>(type: "INTEGER", nullable: true),
                    FilterType = table.Column<int>(type: "INTEGER", nullable: true),
                    FilterEpicId = table.Column<int>(type: "INTEGER", nullable: true),
                    Swimlane = table.Column<int>(type: "INTEGER", nullable: false),
                    CardFields = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockOverWip = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoneDays = table.Column<int>(type: "INTEGER", nullable: true),
                    StartOnActiveSprint = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Boards_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BoardColumns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    WipLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    StatusIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardColumns_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BoardQuickFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardQuickFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardQuickFilters_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardColumns_BoardId",
                table: "BoardColumns",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardQuickFilters_BoardId",
                table: "BoardQuickFilters",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Boards_OneDefaultPerProject",
                table: "Boards",
                column: "ProjectId",
                unique: true,
                filter: "\"IsDefault\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardColumns");

            migrationBuilder.DropTable(
                name: "BoardQuickFilters");

            migrationBuilder.DropTable(
                name: "Boards");
        }
    }
}
