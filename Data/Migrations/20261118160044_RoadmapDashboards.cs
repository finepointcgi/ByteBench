using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoadmapDashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "Issues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dashboards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SharedProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedGroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dashboards_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dashboards_Groups_SharedGroupId",
                        column: x => x.SharedGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Dashboards_Projects_SharedProjectId",
                        column: x => x.SharedProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DashboardGadgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DashboardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Config = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardGadgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardGadgets_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DashboardHomes",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DashboardId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardHomes", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_DashboardHomes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DashboardHomes_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardGadgets_DashboardId_Order",
                table: "DashboardGadgets",
                columns: new[] { "DashboardId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardHomes_DashboardId",
                table: "DashboardHomes",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_OwnerId",
                table: "Dashboards",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_SharedGroupId",
                table: "Dashboards",
                column: "SharedGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_SharedProjectId",
                table: "Dashboards",
                column: "SharedProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardGadgets");

            migrationBuilder.DropTable(
                name: "DashboardHomes");

            migrationBuilder.DropTable(
                name: "Dashboards");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "Issues");
        }
    }
}
