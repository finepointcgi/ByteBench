using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class CiExtras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScheduleId",
                table: "Pipelines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariablesJson",
                table: "Pipelines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowFailure",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentName",
                table: "PipelineJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentUrl",
                table: "PipelineJobs",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtrasJson",
                table: "PipelineJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManual",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlayedAt",
                table: "PipelineJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayedById",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                table: "PipelineJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CiCacheEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiCacheEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiCacheEntries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CiEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiEnvironments_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Keep = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineArtifacts_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PipelineArtifacts_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    VariablesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineSchedules_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PipelineSchedules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    PipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Ref = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deployments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Deployments_CiEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "CiEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deployments_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deployments_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_ScheduleId",
                table: "Pipelines",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineJobs_PlayedById",
                table: "PipelineJobs",
                column: "PlayedById");

            migrationBuilder.CreateIndex(
                name: "IX_CiCacheEntries_RepositoryId_IsProtected_Key",
                table: "CiCacheEntries",
                columns: new[] { "RepositoryId", "IsProtected", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CiCacheEntries_StoredName",
                table: "CiCacheEntries",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironments_RepositoryId_Name",
                table: "CiEnvironments",
                columns: new[] { "RepositoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_EnvironmentId_Id",
                table: "Deployments",
                columns: new[] { "EnvironmentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_JobId",
                table: "Deployments",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_PipelineId",
                table: "Deployments",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_UserId",
                table: "Deployments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_ExpiresAt",
                table: "PipelineArtifacts",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_JobId",
                table: "PipelineArtifacts",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_PipelineId",
                table: "PipelineArtifacts",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_RepositoryId",
                table: "PipelineArtifacts",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_StoredName",
                table: "PipelineArtifacts",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSchedules_Active_NextRunAt",
                table: "PipelineSchedules",
                columns: new[] { "Active", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSchedules_OwnerId",
                table: "PipelineSchedules",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSchedules_RepositoryId",
                table: "PipelineSchedules",
                column: "RepositoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_PipelineJobs_AspNetUsers_PlayedById",
                table: "PipelineJobs",
                column: "PlayedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Pipelines_PipelineSchedules_ScheduleId",
                table: "Pipelines",
                column: "ScheduleId",
                principalTable: "PipelineSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PipelineJobs_AspNetUsers_PlayedById",
                table: "PipelineJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_Pipelines_PipelineSchedules_ScheduleId",
                table: "Pipelines");

            migrationBuilder.DropTable(
                name: "CiCacheEntries");

            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "PipelineArtifacts");

            migrationBuilder.DropTable(
                name: "PipelineSchedules");

            migrationBuilder.DropTable(
                name: "CiEnvironments");

            migrationBuilder.DropIndex(
                name: "IX_Pipelines_ScheduleId",
                table: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_PipelineJobs_PlayedById",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "VariablesJson",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "AllowFailure",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "EnvironmentName",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "EnvironmentUrl",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "ExtrasJson",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "IsManual",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "PlayedAt",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "PlayedById",
                table: "PipelineJobs");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                table: "PipelineJobs");
        }
    }
}
