using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomFieldsTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HoursPerDay",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "OriginalEstimateMinutes",
                table: "Issues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemainingEstimateMinutes",
                table: "Issues",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Required = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Options = table.Column<string>(type: "TEXT", nullable: false),
                    IssueTypes = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFields_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    TitlePrefix = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    LabelIds = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTemplates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LeadId = table.Column<int>(type: "INTEGER", nullable: true),
                    DefaultAssigneeId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectComponents_AspNetUsers_DefaultAssigneeId",
                        column: x => x.DefaultAssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProjectComponents_AspNetUsers_LeadId",
                        column: x => x.LeadId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProjectComponents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsReleased = table.Column<bool>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectVersions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkLogs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkLogs_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    TextValue = table.Column<string>(type: "TEXT", nullable: true),
                    NumberValue = table.Column<double>(type: "REAL", nullable: true),
                    DateValue = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    BoolValue = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_CustomFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "CustomFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueComponents",
                columns: table => new
                {
                    ComponentsId = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuesId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueComponents", x => new { x.ComponentsId, x.IssuesId });
                    table.ForeignKey(
                        name: "FK_IssueComponents_Issues_IssuesId",
                        column: x => x.IssuesId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueComponents_ProjectComponents_ComponentsId",
                        column: x => x.ComponentsId,
                        principalTable: "ProjectComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueVersions",
                columns: table => new
                {
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    VersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueVersions", x => new { x.IssueId, x.VersionId, x.Kind });
                    table.ForeignKey(
                        name: "FK_IssueVersions_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueVersions_ProjectVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "ProjectVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFields_ProjectId_Key",
                table: "CustomFields",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_FieldId_TextValue",
                table: "CustomFieldValues",
                columns: new[] { "FieldId", "TextValue" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_IssueId_FieldId",
                table: "CustomFieldValues",
                columns: new[] { "IssueId", "FieldId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_UserId",
                table: "CustomFieldValues",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueComponents_IssuesId",
                table: "IssueComponents",
                column: "IssuesId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTemplates_ProjectId",
                table: "IssueTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueVersions_VersionId_Kind",
                table: "IssueVersions",
                columns: new[] { "VersionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectComponents_DefaultAssigneeId",
                table: "ProjectComponents",
                column: "DefaultAssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectComponents_LeadId",
                table: "ProjectComponents",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectComponents_ProjectId_Name",
                table: "ProjectComponents",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectVersions_ProjectId_Name",
                table: "ProjectVersions",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_IssueId_Date",
                table: "WorkLogs",
                columns: new[] { "IssueId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkLogs_UserId_Date",
                table: "WorkLogs",
                columns: new[] { "UserId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomFieldValues");

            migrationBuilder.DropTable(
                name: "IssueComponents");

            migrationBuilder.DropTable(
                name: "IssueTemplates");

            migrationBuilder.DropTable(
                name: "IssueVersions");

            migrationBuilder.DropTable(
                name: "WorkLogs");

            migrationBuilder.DropTable(
                name: "CustomFields");

            migrationBuilder.DropTable(
                name: "ProjectComponents");

            migrationBuilder.DropTable(
                name: "ProjectVersions");

            migrationBuilder.DropColumn(
                name: "HoursPerDay",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OriginalEstimateMinutes",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "RemainingEstimateMinutes",
                table: "Issues");
        }
    }
}
