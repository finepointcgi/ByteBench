using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExploreProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_ReporterId",
                table: "Issues");

            migrationBuilder.DropIndex(
                name: "IX_Comments_AuthorId",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ActorId",
                table: "Activities");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectStars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectStars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectStars_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectStars_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    Before = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    After = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CommitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushEvents_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushEvents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushEvents_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ReporterId_CreatedAt",
                table: "Issues",
                columns: new[] { "ReporterId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AuthorId_CreatedAt",
                table: "Comments",
                columns: new[] { "AuthorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActorId_CreatedAt",
                table: "Activities",
                columns: new[] { "ActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectStars_ProjectId",
                table: "ProjectStars",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectStars_UserId_ProjectId",
                table: "ProjectStars",
                columns: new[] { "UserId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushEvents_ProjectId_CreatedAt",
                table: "PushEvents",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PushEvents_RepositoryId",
                table: "PushEvents",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PushEvents_UserId_CreatedAt",
                table: "PushEvents",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectStars");

            migrationBuilder.DropTable(
                name: "PushEvents");

            migrationBuilder.DropIndex(
                name: "IX_Issues_ReporterId_CreatedAt",
                table: "Issues");

            migrationBuilder.DropIndex(
                name: "IX_Comments_AuthorId_CreatedAt",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ActorId_CreatedAt",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ReporterId",
                table: "Issues",
                column: "ReporterId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AuthorId",
                table: "Comments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActorId",
                table: "Activities",
                column: "ActorId");
        }
    }
}
