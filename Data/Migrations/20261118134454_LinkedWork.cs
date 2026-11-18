using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkedWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentId",
                table: "Issues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IssueLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceIssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetIssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueLinks_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IssueLinks_Issues_SourceIssueId",
                        column: x => x.SourceIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueLinks_Issues_TargetIssueId",
                        column: x => x.TargetIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ParentId",
                table: "Issues",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueLinks_CreatedById",
                table: "IssueLinks",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_IssueLinks_SourceIssueId_TargetIssueId_Type",
                table: "IssueLinks",
                columns: new[] { "SourceIssueId", "TargetIssueId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueLinks_TargetIssueId",
                table: "IssueLinks",
                column: "TargetIssueId");

            migrationBuilder.AddForeignKey(
                name: "FK_Issues_Issues_ParentId",
                table: "Issues",
                column: "ParentId",
                principalTable: "Issues",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Issues_Issues_ParentId",
                table: "Issues");

            migrationBuilder.DropTable(
                name: "IssueLinks");

            migrationBuilder.DropIndex(
                name: "IX_Issues_ParentId",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Issues");
        }
    }
}
