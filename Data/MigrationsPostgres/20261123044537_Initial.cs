using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ByteBench.Data.MigrationsPostgres
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Feature = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Model = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdminGrantedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    UsersChecked = table.Column<int>(type: "integer", nullable: false),
                    UsersUpdated = table.Column<int>(type: "integer", nullable: false),
                    UsersDeactivated = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FullPath = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Groups_Groups_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationInvites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedById = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedById = table.Column<int>(type: "integer", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationInvites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApiKeyProtected = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    KeyHint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    KeySetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    KeySetById = table.Column<int>(type: "integer", nullable: true),
                    LastTestAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTestOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastTestJson = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatUserLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TeamId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalUserId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalUserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Nonce = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatUserLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatUserLinks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Group = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonalAccessTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Scopes = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalAccessTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalAccessTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotificationSettings",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastEmailAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SlackDmEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserNotificationSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalGroupMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalGroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalGroupMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalGroupMemberships_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupRedirects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OldPath = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRedirects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupRedirects_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    NextIssueNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AiReviewEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    WikiEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    HoursPerDay = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    WorkbenchWbs = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WorkbenchProjectCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProductionEnvironment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DoraFailureWindowHours = table.Column<int>(type: "integer", nullable: false),
                    AiTriageEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AiTriageAutoApplyLabels = table.Column<bool>(type: "boolean", nullable: false),
                    WorkbenchPushEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Definition = table.Column<string>(type: "text", nullable: false),
                    RunAs = table.Column<int>(type: "integer", nullable: false),
                    AllowRuleTriggers = table.Column<bool>(type: "boolean", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastScheduledRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RoundRobinLastUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                    table.CheckConstraint("CK_AutomationRules_Scope", "(\"ProjectId\" IS NULL) <> (\"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_AutomationRules_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    FollowsWorkflow = table.Column<bool>(type: "boolean", nullable: false),
                    FilterQuery = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FilterLabelIds = table.Column<List<int>>(type: "integer[]", nullable: false),
                    FilterAssigneeId = table.Column<int>(type: "integer", nullable: true),
                    FilterType = table.Column<int>(type: "integer", nullable: true),
                    FilterEpicId = table.Column<int>(type: "integer", nullable: true),
                    Swimlane = table.Column<int>(type: "integer", nullable: false),
                    CardFields = table.Column<int>(type: "integer", nullable: false),
                    BlockOverWip = table.Column<bool>(type: "boolean", nullable: false),
                    DoneDays = table.Column<int>(type: "integer", nullable: true),
                    StartOnActiveSprint = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "CustomFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Options = table.Column<List<string>>(type: "text[]", nullable: false),
                    IssueTypes = table.Column<int[]>(type: "integer[]", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "Dashboards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SharedProjectId = table.Column<int>(type: "integer", nullable: true),
                    SharedGroupId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "DeployFreezeWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CronStart = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CronEnd = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeployFreezeWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeployFreezeWindows_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DeployFreezeWindows_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureFlags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    OffValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IssueNumbers = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureFlags_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FeatureFlags_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagEnvironments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MembersJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagSegments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LocalId = table.Column<int>(type: "integer", nullable: false),
                    ImportId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    TitlePrefix = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    LabelIds = table.Column<List<int>>(type: "integer[]", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
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
                name: "Labels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Labels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Labels_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Labels_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Milestones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Milestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Milestones_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Milestones_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmailPending = table.Column<bool>(type: "boolean", nullable: false),
                    Excerpt = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: true),
                    ChatPending = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Notifications_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "ProjectComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeadId = table.Column<int>(type: "integer", nullable: true),
                    DefaultAssigneeId = table.Column<int>(type: "integer", nullable: true)
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
                name: "ProjectEditorSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    AiEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectEditorSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectEditorSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectPackageSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    KeepLatest = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "ProjectStars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "ProjectVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsReleased = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DefaultBranch = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AllowedMergeStrategies = table.Column<int>(type: "integer", nullable: true),
                    DefaultMergeStrategy = table.Column<int>(type: "integer", nullable: true),
                    ForkedFromId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repositories_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Repositories_Repositories_ForkedFromId",
                        column: x => x.ForkedFromId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SavedFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedFilters_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedFilters_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskSettings",
                columns: table => new
                {
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PublicFormEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CustomAddress = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    DefaultIssueType = table.Column<int>(type: "integer", nullable: false),
                    DefaultLabelIds = table.Column<List<int>>(type: "integer[]", nullable: false),
                    DefaultAssigneeId = table.Column<int>(type: "integer", nullable: true),
                    IssueTemplate = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    AutoReplyEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AutoReplyTemplate = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    NotifyStatusIds = table.Column<List<int>>(type: "integer[]", nullable: false),
                    ResponseTargetHours = table.Column<int>(type: "integer", nullable: false),
                    FormIntro = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskSettings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ServiceDeskSettings_AspNetUsers_DefaultAssigneeId",
                        column: x => x.DefaultAssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ServiceDeskSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Snippets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    CurrentRevision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snippets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Snippets_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Snippets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sprints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommittedCount = table.Column<int>(type: "integer", nullable: true),
                    CommittedPoints = table.Column<int>(type: "integer", nullable: true),
                    CompletedCount = table.Column<int>(type: "integer", nullable: true),
                    CompletedPoints = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sprints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sprints_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackerImportJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    RequestedById = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceProject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IssuesExpected = table.Column<int>(type: "integer", nullable: false),
                    IssuesRead = table.Column<int>(type: "integer", nullable: false),
                    IssuesCreated = table.Column<int>(type: "integer", nullable: false),
                    IssuesSkipped = table.Column<int>(type: "integer", nullable: false),
                    CommentsCreated = table.Column<int>(type: "integer", nullable: false),
                    LabelsCreated = table.Column<int>(type: "integer", nullable: false),
                    MilestonesCreated = table.Column<int>(type: "integer", nullable: false),
                    LinksCreated = table.Column<int>(type: "integer", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    WarningsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackerImportJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackerImportJobs_AspNetUsers_RequestedById",
                        column: x => x.RequestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TrackerImportJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Secret = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Events = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    DisabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastDeliveryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ChatSettings = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    GroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhooks_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Webhooks_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Webhooks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiPageIndex",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPageIndex", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPageIndex_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    WbsCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WorkbenchProjectCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Percent = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchOutbox_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchPlanSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    RootWbsCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EpicLevels = table.Column<int>(type: "integer", nullable: false),
                    ReconcileEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Processed = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReportJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchPlanSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchPlanSyncs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchTaskSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    WbsCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastPercent = table.Column<decimal>(type: "numeric", nullable: true),
                    LastStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastPushedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchTaskSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchTaskSyncs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowStatuses_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: true),
                    IssueKey = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    PullRequestId = table.Column<int>(type: "integer", nullable: true),
                    ActorId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Actions = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuns_AutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BoardColumns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BoardId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    WipLimit = table.Column<int>(type: "integer", nullable: true),
                    StatusIds = table.Column<List<int>>(type: "integer[]", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BoardId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Query = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "DashboardGadgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DashboardId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Config = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false)
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
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    DashboardId = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "FlagChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    FlagId = table.Column<int>(type: "integer", nullable: false),
                    EnvironmentId = table.Column<int>(type: "integer", nullable: true),
                    EnvironmentName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActorId = table.Column<int>(type: "integer", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagChanges_FeatureFlags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagEnvironmentStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FlagId = table.Column<int>(type: "integer", nullable: false),
                    EnvironmentId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RulesJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<int>(type: "integer", nullable: true),
                    LastEvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagEnvironmentStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_FeatureFlags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "FeatureFlags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FlagEnvironmentStates_FlagEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "FlagEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FlagSdkKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnvironmentId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    RotatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagSdkKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlagSdkKeys_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FlagSdkKeys_FlagEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "FlagEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContainerBlobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageId = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    IsManifest = table.Column<bool>(type: "boolean", nullable: false),
                    MediaType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageId = table.Column<int>(type: "integer", nullable: false),
                    Tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ManifestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    PublishedById = table.Column<int>(type: "integer", nullable: true),
                    PipelineId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "BranchProtectionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    RequiredApprovals = table.Column<int>(type: "integer", nullable: false),
                    BlockDirectPushes = table.Column<bool>(type: "boolean", nullable: false),
                    BlockForcePushes = table.Column<bool>(type: "boolean", nullable: false),
                    BlockDeletion = table.Column<bool>(type: "boolean", nullable: false),
                    RequireChecksToPass = table.Column<bool>(type: "boolean", nullable: false),
                    DismissStaleApprovals = table.Column<bool>(type: "boolean", nullable: false),
                    AllowAdminBypass = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultReviewerIds = table.Column<List<int>>(type: "integer[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequireThreadResolution = table.Column<bool>(type: "boolean", nullable: false),
                    RequireCodeOwnerApproval = table.Column<bool>(type: "boolean", nullable: false),
                    MergeQueueEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MergeQueueMaxBatch = table.Column<int>(type: "integer", nullable: false),
                    MergeQueueRequireCi = table.Column<bool>(type: "boolean", nullable: false),
                    MergeQueueStrategy = table.Column<int>(type: "integer", nullable: true),
                    MergeQueueTimeoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    MergeQueueApprovalTimeoutMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchProtectionRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchProtectionRules_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CiCacheEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsProtected = table.Column<bool>(type: "boolean", nullable: false),
                    StoredName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequiredApprovals = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    StoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsProtected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "CiVariables",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EncryptedValue = table.Column<string>(type: "text", nullable: false),
                    IsProtected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiVariables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiVariables_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CiVariables_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CiVariables_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EditorWorkspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Branch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditorWorkspaces_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EditorWorkspaces_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LfsObjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Oid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    UploadedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "PagesSites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    NotFound = table.Column<int>(type: "integer", nullable: false),
                    ReviewSites = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewExpireDays = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagesSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagesSites_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Branch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    VariablesJson = table.Column<string>(type: "text", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPipelineId = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "ProtectedTagRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    CreateRole = table.Column<int>(type: "integer", nullable: false),
                    BlockDeletion = table.Column<bool>(type: "boolean", nullable: false),
                    BlockUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "PullRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SourceBranch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    TargetBranch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MergedById = table.Column<int>(type: "integer", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SourceCommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TargetCommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsDraft = table.Column<bool>(type: "boolean", nullable: false),
                    MergeStrategy = table.Column<int>(type: "integer", nullable: true),
                    SourceRepositoryId = table.Column<int>(type: "integer", nullable: true),
                    IsCrossFork = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequests_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PullRequests_AspNetUsers_MergedById",
                        column: x => x.MergedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PullRequests_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequests_Repositories_SourceRepositoryId",
                        column: x => x.SourceRepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PushEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Before = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    After = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CommitCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    TagName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsPrerelease = table.Column<bool>(type: "boolean", nullable: false),
                    IsDraft = table.Column<bool>(type: "boolean", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Releases_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Releases_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryAiReviewSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    MaxDiffChars = table.Column<int>(type: "integer", nullable: false),
                    IgnorePaths = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    FocusPrompt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BlockOnUnresolvedFindings = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryAiReviewSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryAiReviewSettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    RequestedById = table.Column<int>(type: "integer", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DefaultBranch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    KeepAsMirror = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BytesReceived = table.Column<long>(type: "bigint", nullable: false),
                    BranchCount = table.Column<int>(type: "integer", nullable: false),
                    TagCount = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryImports_AspNetUsers_RequestedById",
                        column: x => x.RequestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RepositoryImports_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RepositoryImports_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryMirrors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    OnlyProtectedBranches = table.Column<bool>(type: "boolean", nullable: false),
                    LastStatus = table.Column<int>(type: "integer", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NextSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "RepositoryRedirects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    OldSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "RepositorySecuritySettings",
                columns: table => new
                {
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    MergeCheckEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MergeCheckSeverity = table.Column<int>(type: "integer", nullable: false),
                    NotifySeverity = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySecuritySettings", x => x.RepositoryId);
                    table.ForeignKey(
                        name: "FK_RepositorySecuritySettings_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityTriages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    By = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityTriages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityTriages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityTriages_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SshKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    RepositoryId = table.Column<int>(type: "integer", nullable: true),
                    CanWrite = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SshKeys_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SshKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SshKeys_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedFilterStars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SavedFilterId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedFilterStars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedFilterStars_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedFilterStars_SavedFilters_SavedFilterId",
                        column: x => x.SavedFilterId,
                        principalTable: "SavedFilters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnippetComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnippetId = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnippetComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnippetComments_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SnippetComments_Snippets_SnippetId",
                        column: x => x.SnippetId,
                        principalTable: "Snippets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnippetRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnippetId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnippetRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnippetRevisions_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SnippetRevisions_Snippets_SnippetId",
                        column: x => x.SnippetId,
                        principalTable: "Snippets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatIntegrationOptOuts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    WebhookId = table.Column<int>(type: "integer", nullable: false),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatIntegrationOptOuts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatIntegrationOptOuts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatIntegrationOptOuts_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WebhookId = table.Column<int>(type: "integer", nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Event = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    IsRedelivery = table.Column<bool>(type: "boolean", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    ResponseBody = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Webhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "Webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Issues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    StoryPoints = table.Column<int>(type: "integer", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortOrder = table.Column<double>(type: "double precision", nullable: false),
                    AssigneeId = table.Column<int>(type: "integer", nullable: true),
                    ReporterId = table.Column<int>(type: "integer", nullable: false),
                    MilestoneId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    SprintId = table.Column<int>(type: "integer", nullable: true),
                    WorkflowStatusId = table.Column<int>(type: "integer", nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OriginalEstimateMinutes = table.Column<int>(type: "integer", nullable: true),
                    RemainingEstimateMinutes = table.Column<int>(type: "integer", nullable: true),
                    WorkbenchWbs = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Issues_AspNetUsers_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Issues_AspNetUsers_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Issues_Issues_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Issues_Milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "Milestones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Issues_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Issues_Sprints_SprintId",
                        column: x => x.SprintId,
                        principalTable: "Sprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Issues_WorkflowStatuses_WorkflowStatusId",
                        column: x => x.WorkflowStatusId,
                        principalTable: "WorkflowStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTransitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    FromStatusId = table.Column<int>(type: "integer", nullable: false),
                    ToStatusId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_WorkflowStatuses_FromStatusId",
                        column: x => x.FromStatusId,
                        principalTable: "WorkflowStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_WorkflowStatuses_ToStatusId",
                        column: x => x.ToStatusId,
                        principalTable: "WorkflowStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageVersionId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sha1 = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Sha512 = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "CiEnvironmentApprovers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnvironmentId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    GroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CiEnvironmentApprovers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CiEnvironmentApprovers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CiEnvironmentApprovers_CiEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "CiEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CiEnvironmentApprovers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EditorWorkspaceChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkspaceId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OriginalPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorWorkspaceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditorWorkspaceChanges_EditorWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "EditorWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PagesDeployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SiteId = table.Column<int>(type: "integer", nullable: false),
                    Slot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PipelineId = table.Column<int>(type: "integer", nullable: true),
                    JobId = table.Column<int>(type: "integer", nullable: true),
                    DeployedById = table.Column<int>(type: "integer", nullable: true),
                    Directory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    IsRollback = table.Column<bool>(type: "boolean", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PagesDeployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PagesDeployments_AspNetUsers_DeployedById",
                        column: x => x.DeployedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PagesDeployments_PagesSites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "PagesSites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Backend = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    DiffChars = table.Column<int>(type: "integer", nullable: false),
                    PullRequestId = table.Column<int>(type: "integer", nullable: false),
                    HeadSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedById = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    FindingsCount = table.Column<int>(type: "integer", nullable: false),
                    InlineCount = table.Column<int>(type: "integer", nullable: false),
                    UnplacedCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedFilesCount = table.Column<int>(type: "integer", nullable: false),
                    BatchCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiReviews_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CommitMessage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PullRequestId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    TriggeredById = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsForkPullRequest = table.Column<bool>(type: "boolean", nullable: false),
                    AwaitingApproval = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovedById = table.Column<int>(type: "integer", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScheduleId = table.Column<int>(type: "integer", nullable: true),
                    VariablesJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pipelines_AspNetUsers_ApprovedById",
                        column: x => x.ApprovedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_AspNetUsers_TriggeredById",
                        column: x => x.TriggeredById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_PipelineSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "PipelineSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Pipelines_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PullRequestActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PullRequestId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestActivities_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PullRequestActivities_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PullRequestComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AiReviewId = table.Column<int>(type: "integer", nullable: true),
                    AiSeverity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AiFingerprint = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AiDismissed = table.Column<bool>(type: "boolean", nullable: false),
                    PullRequestId = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Line = table.Column<int>(type: "integer", nullable: true),
                    OldLine = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    IsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedById = table.Column<int>(type: "integer", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    StartLine = table.Column<int>(type: "integer", nullable: true),
                    LineText = table.Column<string>(type: "text", nullable: true),
                    AppliedCommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestComments_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PullRequestComments_AspNetUsers_ResolvedById",
                        column: x => x.ResolvedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PullRequestComments_PullRequestComments_ParentId",
                        column: x => x.ParentId,
                        principalTable: "PullRequestComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequestComments_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PullRequestReviewers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PullRequestId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    AddedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestReviewers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_AspNetUsers_AddedById",
                        column: x => x.AddedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequestReviewers_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PullRequestReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PullRequestId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequestReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequestReviews_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PullRequestReviews_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReleaseId = table.Column<int>(type: "integer", nullable: false),
                    StoredName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploaderId = table.Column<int>(type: "integer", nullable: false),
                    DownloadCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReleaseAssets_AspNetUsers_UploaderId",
                        column: x => x.UploaderId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReleaseAssets_Releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "Releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnippetFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RevisionId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnippetFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnippetFiles_SnippetRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "SnippetRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activities_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Activities_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    StoredName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploaderId = table.Column<int>(type: "integer", nullable: false),
                    FromComment = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_AspNetUsers_UploaderId",
                        column: x => x.UploaderId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Attachments_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServiceDeskContactId = table.Column<int>(type: "integer", nullable: true),
                    IsPublicReply = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Comments_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Comments_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Comments_ServiceDeskContacts_ServiceDeskContactId",
                        column: x => x.ServiceDeskContactId,
                        principalTable: "ServiceDeskContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    FieldId = table.Column<int>(type: "integer", nullable: false),
                    TextValue = table.Column<string>(type: "text", nullable: true),
                    NumberValue = table.Column<double>(type: "double precision", nullable: true),
                    DateValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true)
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
                    ComponentsId = table.Column<int>(type: "integer", nullable: false),
                    IssuesId = table.Column<int>(type: "integer", nullable: false)
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
                name: "IssueLabel",
                columns: table => new
                {
                    IssuesId = table.Column<int>(type: "integer", nullable: false),
                    LabelsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueLabel", x => new { x.IssuesId, x.LabelsId });
                    table.ForeignKey(
                        name: "FK_IssueLabel_Issues_IssuesId",
                        column: x => x.IssuesId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueLabel_Labels_LabelsId",
                        column: x => x.LabelsId,
                        principalTable: "Labels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceIssueId = table.Column<int>(type: "integer", nullable: false),
                    TargetIssueId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    CreatedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "IssueTriageRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedById = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTriageRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTriageRuns_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueVersions",
                columns: table => new
                {
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    VersionId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "ServiceDeskTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ContactId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ReplyToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TrackingToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    InitialMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRequesterActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstResponseAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAgentReplyAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceDeskTickets_ServiceDeskContacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "ServiceDeskContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Watches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: true),
                    PullRequestId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Muted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watches", x => x.Id);
                    table.CheckConstraint("CK_Watch_OneTarget", "(\"IssueId\" IS NULL) <> (\"PullRequestId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Watches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Watches_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Watches_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkbenchIssueLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Missing = table.Column<bool>(type: "boolean", nullable: false),
                    MissingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkbenchIssueLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkbenchIssueLinks_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkbenchIssueLinks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MergeQueueEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    PullRequestId = table.Column<int>(type: "integer", nullable: false),
                    TargetBranch = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    EnqueuedById = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Strategy = table.Column<int>(type: "integer", nullable: false),
                    HeadSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BaseSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SpeculativeSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PipelineId = table.Column<int>(type: "integer", nullable: true),
                    Builds = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RemovedById = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TestingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AwaitingApproval = table.Column<bool>(type: "boolean", nullable: false),
                    AwaitingApprovalSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeleteSourceBranch = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeQueueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_AspNetUsers_EnqueuedById",
                        column: x => x.EnqueuedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_AspNetUsers_RemovedById",
                        column: x => x.RemovedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_PullRequests_PullRequestId",
                        column: x => x.PullRequestId,
                        principalTable: "PullRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MergeQueueEntries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnvironmentAction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PipelineId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Image = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Needs = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    AllowFailure = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    EnvironmentName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EnvironmentUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExtrasJson = table.Column<string>(type: "text", nullable: true),
                    PlayedById = table.Column<int>(type: "integer", nullable: true),
                    PlayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineJobs_AspNetUsers_PlayedById",
                        column: x => x.PlayedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PipelineJobs_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    PipelineId = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ToolVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Critical = table.Column<int>(type: "integer", nullable: false),
                    High = table.Column<int>(type: "integer", nullable: false),
                    Medium = table.Column<int>(type: "integer", nullable: false),
                    Low = table.Column<int>(type: "integer", nullable: false),
                    Other = table.Column<int>(type: "integer", nullable: false),
                    Dismissed = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityScans_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SecurityScans_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueTriageItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<int>(type: "integer", nullable: true),
                    Value = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ResolvedById = table.Column<int>(type: "integer", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueTriageItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueTriageItems_IssueTriageRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "IssueTriageRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceDeskMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CommentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceDeskMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceDeskMessages_ServiceDeskTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "ServiceDeskTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    RedeployOfId = table.Column<int>(type: "integer", nullable: true),
                    IsRollback = table.Column<bool>(type: "boolean", nullable: false),
                    EnvironmentId = table.Column<int>(type: "integer", nullable: false),
                    PipelineId = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Ref = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                        name: "FK_Deployments_Deployments_RedeployOfId",
                        column: x => x.RedeployOfId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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

            migrationBuilder.CreateTable(
                name: "PipelineArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    PipelineId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    StoredName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Keep = table.Column<bool>(type: "boolean", nullable: false)
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
                name: "PipelineSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Run = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineSteps_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    RepositoryId = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    PackageName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PackageVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Ecosystem = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FixedVersions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Line = table.Column<int>(type: "integer", nullable: true),
                    Engines = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Aliases = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HelpUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Context = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReportedTriage = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityFindings_SecurityScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SecurityScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentApprovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    PipelineId = table.Column<int>(type: "integer", nullable: false),
                    DeploymentId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsStale = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentApprovals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeploymentApprovals_Deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DeploymentApprovals_PipelineJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PipelineJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineStepLogs",
                columns: table => new
                {
                    StepId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStepLogs", x => x.StepId);
                    table.ForeignKey(
                        name: "FK_PipelineStepLogs_PipelineSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "PipelineSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ActorId_CreatedAt",
                table: "Activities",
                columns: new[] { "ActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_IssueId",
                table: "Activities",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_AiReviews_PullRequestId_HeadSha",
                table: "AiReviews",
                columns: new[] { "PullRequestId", "HeadSha" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_IssueId_CreatedAt",
                table: "Attachments",
                columns: new[] { "IssueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_StoredName",
                table: "Attachments",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_UploaderId",
                table: "Attachments",
                column: "UploaderId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action",
                table: "AuditEvents",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorId_CreatedAt",
                table: "AuditEvents",
                columns: new[] { "ActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CreatedAt",
                table: "AuditEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ProjectId_CreatedAt",
                table: "AuditEvents",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_AuthorId",
                table: "AutomationRules",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_GroupId",
                table: "AutomationRules",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_ProjectId",
                table: "AutomationRules",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_RuleId_StartedAt",
                table: "AutomationRuns",
                columns: new[] { "RuleId", "StartedAt" });

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
                filter: "\"IsDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_BranchProtectionRules_RepositoryId_Pattern",
                table: "BranchProtectionRules",
                columns: new[] { "RepositoryId", "Pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatIntegrationOptOuts_ProjectId_WebhookId",
                table: "ChatIntegrationOptOuts",
                columns: new[] { "ProjectId", "WebhookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatIntegrationOptOuts_WebhookId",
                table: "ChatIntegrationOptOuts",
                column: "WebhookId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_Nonce",
                table: "ChatUserLinks",
                column: "Nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_Provider_TeamId_ExternalUserId",
                table: "ChatUserLinks",
                columns: new[] { "Provider", "TeamId", "ExternalUserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUserLinks_UserId",
                table: "ChatUserLinks",
                column: "UserId");

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
                name: "IX_CiEnvironmentApprovers_EnvironmentId",
                table: "CiEnvironmentApprovers",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironmentApprovers_GroupId",
                table: "CiEnvironmentApprovers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironmentApprovers_UserId",
                table: "CiEnvironmentApprovers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CiEnvironments_RepositoryId_Name",
                table: "CiEnvironments",
                columns: new[] { "RepositoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CiVariables_ProjectId_RepositoryId_Name",
                table: "CiVariables",
                columns: new[] { "ProjectId", "RepositoryId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CiVariables_RepositoryId",
                table: "CiVariables",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CiVariables_UpdatedById",
                table: "CiVariables",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AuthorId_CreatedAt",
                table: "Comments",
                columns: new[] { "AuthorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_IssueId",
                table: "Comments",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ServiceDeskContactId",
                table: "Comments",
                column: "ServiceDeskContactId");

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

            migrationBuilder.CreateIndex(
                name: "IX_DeployFreezeWindows_CreatedById",
                table: "DeployFreezeWindows",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_DeployFreezeWindows_ProjectId",
                table: "DeployFreezeWindows",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentApprovals_DeploymentId",
                table: "DeploymentApprovals",
                column: "DeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentApprovals_JobId_IsStale",
                table: "DeploymentApprovals",
                columns: new[] { "JobId", "IsStale" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentApprovals_UserId",
                table: "DeploymentApprovals",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_CommitSha",
                table: "Deployments",
                column: "CommitSha");

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
                name: "IX_Deployments_RedeployOfId",
                table: "Deployments",
                column: "RedeployOfId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_UserId",
                table: "Deployments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySyncRuns_Source_StartedAt",
                table: "DirectorySyncRuns",
                columns: new[] { "Source", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EditorWorkspaceChanges_WorkspaceId_Path",
                table: "EditorWorkspaceChanges",
                columns: new[] { "WorkspaceId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EditorWorkspaces_RepositoryId",
                table: "EditorWorkspaces",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_EditorWorkspaces_UserId_RepositoryId_Branch",
                table: "EditorWorkspaces",
                columns: new[] { "UserId", "RepositoryId", "Branch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMemberships_GroupId",
                table: "ExternalGroupMemberships",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGroupMemberships_UserId_GroupId_Source",
                table: "ExternalGroupMemberships",
                columns: new[] { "UserId", "GroupId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_CreatedById",
                table: "FeatureFlags",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FeatureFlags_ProjectId_Key",
                table: "FeatureFlags",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagChanges_FlagId_At",
                table: "FlagChanges",
                columns: new[] { "FlagId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironments_ProjectId_Name",
                table: "FlagEnvironments",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_EnvironmentId",
                table: "FlagEnvironmentStates",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_FlagId_EnvironmentId",
                table: "FlagEnvironmentStates",
                columns: new[] { "FlagId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagEnvironmentStates_UpdatedById",
                table: "FlagEnvironmentStates",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_CreatedById",
                table: "FlagSdkKeys",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_EnvironmentId",
                table: "FlagSdkKeys",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FlagSdkKeys_KeyHash",
                table: "FlagSdkKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagSegments_ProjectId_Key",
                table: "FlagSegments",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_GroupId_UserId",
                table: "GroupMembers",
                columns: new[] { "GroupId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserId",
                table: "GroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupRedirects_GroupId",
                table: "GroupRedirects",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupRedirects_OldPath",
                table: "GroupRedirects",
                column: "OldPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Groups_FullPath",
                table: "Groups",
                column: "FullPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Groups_ParentId",
                table: "Groups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedItems_ProjectId_SourceKey_Kind_ExternalId",
                table: "ImportedItems",
                columns: new[] { "ProjectId", "SourceKey", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueComponents_IssuesId",
                table: "IssueComponents",
                column: "IssuesId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueLabel_LabelsId",
                table: "IssueLabel",
                column: "LabelsId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Issues_AssigneeId",
                table: "Issues",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_MilestoneId",
                table: "Issues",
                column: "MilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ParentId",
                table: "Issues",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ReporterId_CreatedAt",
                table: "Issues",
                columns: new[] { "ReporterId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_SprintId",
                table: "Issues",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_WorkflowStatusId",
                table: "Issues",
                column: "WorkflowStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTemplates_ProjectId",
                table: "IssueTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTriageItems_RunId",
                table: "IssueTriageItems",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueTriageRuns_IssueId_CreatedAt",
                table: "IssueTriageRuns",
                columns: new[] { "IssueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueVersions_VersionId_Kind",
                table: "IssueVersions",
                columns: new[] { "VersionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Labels_GroupId_Name",
                table: "Labels",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Labels_ProjectId_Name",
                table: "Labels",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

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
                name: "IX_MergeQueueEntries_EnqueuedById",
                table: "MergeQueueEntries",
                column: "EnqueuedById");

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_PipelineId",
                table: "MergeQueueEntries",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_PullRequestId_State",
                table: "MergeQueueEntries",
                columns: new[] { "PullRequestId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_RemovedById",
                table: "MergeQueueEntries",
                column: "RemovedById");

            migrationBuilder.CreateIndex(
                name: "IX_MergeQueueEntries_RepositoryId_TargetBranch_State_Position",
                table: "MergeQueueEntries",
                columns: new[] { "RepositoryId", "TargetBranch", "State", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Milestones_GroupId",
                table: "Milestones",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Milestones_ProjectId",
                table: "Milestones",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_UserId_Group",
                table: "NotificationPreferences",
                columns: new[] { "UserId", "Group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ActorId",
                table: "Notifications",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EmailPending",
                table: "Notifications",
                column: "EmailPending",
                filter: "\"EmailPending\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ProjectId",
                table: "Notifications",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "IsRead", "CreatedAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_DeployedById",
                table: "PagesDeployments",
                column: "DeployedById");

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_ExpiresAt",
                table: "PagesDeployments",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_SiteId_Slot_IsCurrent",
                table: "PagesDeployments",
                columns: new[] { "SiteId", "Slot", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_PagesDeployments_SiteId_Slot_Version",
                table: "PagesDeployments",
                columns: new[] { "SiteId", "Slot", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PagesSites_RepositoryId",
                table: "PagesSites",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccessTokens_TokenHash",
                table: "PersonalAccessTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccessTokens_UserId",
                table: "PersonalAccessTokens",
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
                name: "IX_PipelineJobs_PipelineId",
                table: "PipelineJobs",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineJobs_PlayedById",
                table: "PipelineJobs",
                column: "PlayedById");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_ApprovedById",
                table: "Pipelines",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_PullRequestId",
                table: "Pipelines",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_RepositoryId_CommitSha",
                table: "Pipelines",
                columns: new[] { "RepositoryId", "CommitSha" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_RepositoryId_Id",
                table: "Pipelines",
                columns: new[] { "RepositoryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_ScheduleId",
                table: "Pipelines",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_Status",
                table: "Pipelines",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Pipelines_TriggeredById",
                table: "Pipelines",
                column: "TriggeredById");

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

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSteps_JobId",
                table: "PipelineSteps",
                column: "JobId");

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
                name: "IX_ProjectMembers_ProjectId_UserId",
                table: "ProjectMembers",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_UserId",
                table: "ProjectMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_GroupId",
                table: "Projects",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Key",
                table: "Projects",
                column: "Key",
                unique: true);

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
                name: "IX_ProjectVersions_ProjectId_Name",
                table: "ProjectVersions",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProtectedTagRules_RepositoryId_Pattern",
                table: "ProtectedTagRules",
                columns: new[] { "RepositoryId", "Pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestActivities_ActorId",
                table: "PullRequestActivities",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestActivities_PullRequestId",
                table: "PullRequestActivities",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_AuthorId",
                table: "PullRequestComments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_ParentId",
                table: "PullRequestComments",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_PullRequestId_AiFingerprint",
                table: "PullRequestComments",
                columns: new[] { "PullRequestId", "AiFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestComments_ResolvedById",
                table: "PullRequestComments",
                column: "ResolvedById");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_AddedById",
                table: "PullRequestReviewers",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_PullRequestId_UserId",
                table: "PullRequestReviewers",
                columns: new[] { "PullRequestId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviewers_UserId",
                table: "PullRequestReviewers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviews_PullRequestId_UserId",
                table: "PullRequestReviews",
                columns: new[] { "PullRequestId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequestReviews_UserId",
                table: "PullRequestReviews",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_AuthorId",
                table: "PullRequests",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_MergedById",
                table: "PullRequests",
                column: "MergedById");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_RepositoryId_Number",
                table: "PullRequests",
                columns: new[] { "RepositoryId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_SourceRepositoryId_State",
                table: "PullRequests",
                columns: new[] { "SourceRepositoryId", "State" });

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

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationInvites_TokenHash",
                table: "RegistrationInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAssets_ReleaseId",
                table: "ReleaseAssets",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAssets_StoredName",
                table: "ReleaseAssets",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseAssets_UploaderId",
                table: "ReleaseAssets",
                column: "UploaderId");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_AuthorId",
                table: "Releases",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_RepositoryId_CreatedAt",
                table: "Releases",
                columns: new[] { "RepositoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Releases_RepositoryId_TagName",
                table: "Releases",
                columns: new[] { "RepositoryId", "TagName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ForkedFromId",
                table: "Repositories",
                column: "ForkedFromId");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ProjectId_Slug",
                table: "Repositories",
                columns: new[] { "ProjectId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryAiReviewSettings_RepositoryId",
                table: "RepositoryAiReviewSettings",
                column: "RepositoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryImports_ProjectId_Status",
                table: "RepositoryImports",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryImports_RepositoryId",
                table: "RepositoryImports",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryImports_RequestedById",
                table: "RepositoryImports",
                column: "RequestedById");

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

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRedirects_ProjectId_OldSlug",
                table: "RepositoryRedirects",
                columns: new[] { "ProjectId", "OldSlug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryRedirects_RepositoryId",
                table: "RepositoryRedirects",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilters_OwnerId",
                table: "SavedFilters",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilters_ProjectId_OwnerId",
                table: "SavedFilters",
                columns: new[] { "ProjectId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilterStars_SavedFilterId",
                table: "SavedFilterStars",
                column: "SavedFilterId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilterStars_UserId_SavedFilterId",
                table: "SavedFilterStars",
                columns: new[] { "UserId", "SavedFilterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityFindings_RepositoryId_Fingerprint",
                table: "SecurityFindings",
                columns: new[] { "RepositoryId", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityFindings_ScanId_Fingerprint",
                table: "SecurityFindings",
                columns: new[] { "ScanId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityScans_PipelineId",
                table: "SecurityScans",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityScans_RepositoryId_CommitSha",
                table: "SecurityScans",
                columns: new[] { "RepositoryId", "CommitSha" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityScans_RepositoryId_Ref_PipelineId",
                table: "SecurityScans",
                columns: new[] { "RepositoryId", "Ref", "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityTriages_RepositoryId_Fingerprint",
                table: "SecurityTriages",
                columns: new[] { "RepositoryId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityTriages_UserId",
                table: "SecurityTriages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskContacts_Email",
                table: "ServiceDeskContacts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskMessages_MessageId",
                table: "ServiceDeskMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskMessages_TicketId",
                table: "ServiceDeskMessages",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskSettings_CustomAddress",
                table: "ServiceDeskSettings",
                column: "CustomAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskSettings_DefaultAssigneeId",
                table: "ServiceDeskSettings",
                column: "DefaultAssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ContactId",
                table: "ServiceDeskTickets",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_IssueId",
                table: "ServiceDeskTickets",
                column: "IssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ProjectId_LastRequesterActivityAt",
                table: "ServiceDeskTickets",
                columns: new[] { "ProjectId", "LastRequesterActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_ReplyToken",
                table: "ServiceDeskTickets",
                column: "ReplyToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceDeskTickets_TrackingToken",
                table: "ServiceDeskTickets",
                column: "TrackingToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnippetComments_AuthorId",
                table: "SnippetComments",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SnippetComments_SnippetId_CreatedAt",
                table: "SnippetComments",
                columns: new[] { "SnippetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SnippetFiles_RevisionId_Position",
                table: "SnippetFiles",
                columns: new[] { "RevisionId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_SnippetRevisions_AuthorId",
                table: "SnippetRevisions",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SnippetRevisions_SnippetId_Number",
                table: "SnippetRevisions",
                columns: new[] { "SnippetId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Snippets_AuthorId_UpdatedAt",
                table: "Snippets",
                columns: new[] { "AuthorId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Snippets_ProjectId_UpdatedAt",
                table: "Snippets",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_OneActivePerProject",
                table: "Sprints",
                column: "ProjectId",
                unique: true,
                filter: "\"State\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Sprints_ProjectId_State",
                table: "Sprints",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_CreatedById",
                table: "SshKeys",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_Fingerprint",
                table: "SshKeys",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_RepositoryId",
                table: "SshKeys",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_UserId",
                table: "SshKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackerImportJobs_ProjectId_Status",
                table: "TrackerImportJobs",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackerImportJobs_RequestedById",
                table: "TrackerImportJobs",
                column: "RequestedById");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_IssueId",
                table: "Watches",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_PullRequestId",
                table: "Watches",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_UserId_IssueId",
                table: "Watches",
                columns: new[] { "UserId", "IssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watches_UserId_PullRequestId",
                table: "Watches",
                columns: new[] { "UserId", "PullRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WebhookId_CreatedAt",
                table: "WebhookDeliveries",
                columns: new[] { "WebhookId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_CreatedById",
                table: "Webhooks",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_GroupId",
                table: "Webhooks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_ProjectId",
                table: "Webhooks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPageIndex_ProjectId_Path",
                table: "WikiPageIndex",
                columns: new[] { "ProjectId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchIssueLinks_IssueId",
                table: "WorkbenchIssueLinks",
                column: "IssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchIssueLinks_ProjectId_TaskId",
                table: "WorkbenchIssueLinks",
                columns: new[] { "ProjectId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchOutbox_ProjectId_CreatedAt",
                table: "WorkbenchOutbox",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchOutbox_State_NextAttemptAt",
                table: "WorkbenchOutbox",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchPlanSyncs_ProjectId",
                table: "WorkbenchPlanSyncs",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkbenchTaskSyncs_ProjectId_WbsCode",
                table: "WorkbenchTaskSyncs",
                columns: new[] { "ProjectId", "WbsCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStatuses_ProjectId_Order",
                table: "WorkflowStatuses",
                columns: new[] { "ProjectId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_FromStatusId_ToStatusId",
                table: "WorkflowTransitions",
                columns: new[] { "FromStatusId", "ToStatusId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_ProjectId",
                table: "WorkflowTransitions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_ToStatusId",
                table: "WorkflowTransitions",
                column: "ToStatusId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "AiReviews");

            migrationBuilder.DropTable(
                name: "AiUsages");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropTable(
                name: "BoardColumns");

            migrationBuilder.DropTable(
                name: "BoardQuickFilters");

            migrationBuilder.DropTable(
                name: "BranchProtectionRules");

            migrationBuilder.DropTable(
                name: "ChatIntegrationOptOuts");

            migrationBuilder.DropTable(
                name: "ChatUserLinks");

            migrationBuilder.DropTable(
                name: "CiCacheEntries");

            migrationBuilder.DropTable(
                name: "CiEnvironmentApprovers");

            migrationBuilder.DropTable(
                name: "CiVariables");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "ContainerBlobs");

            migrationBuilder.DropTable(
                name: "CustomFieldValues");

            migrationBuilder.DropTable(
                name: "DashboardGadgets");

            migrationBuilder.DropTable(
                name: "DashboardHomes");

            migrationBuilder.DropTable(
                name: "DeployFreezeWindows");

            migrationBuilder.DropTable(
                name: "DeploymentApprovals");

            migrationBuilder.DropTable(
                name: "DirectorySyncRuns");

            migrationBuilder.DropTable(
                name: "EditorWorkspaceChanges");

            migrationBuilder.DropTable(
                name: "ExternalGroupMemberships");

            migrationBuilder.DropTable(
                name: "FlagChanges");

            migrationBuilder.DropTable(
                name: "FlagEnvironmentStates");

            migrationBuilder.DropTable(
                name: "FlagSdkKeys");

            migrationBuilder.DropTable(
                name: "FlagSegments");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "GroupRedirects");

            migrationBuilder.DropTable(
                name: "ImportedItems");

            migrationBuilder.DropTable(
                name: "IssueComponents");

            migrationBuilder.DropTable(
                name: "IssueLabel");

            migrationBuilder.DropTable(
                name: "IssueLinks");

            migrationBuilder.DropTable(
                name: "IssueTemplates");

            migrationBuilder.DropTable(
                name: "IssueTriageItems");

            migrationBuilder.DropTable(
                name: "IssueVersions");

            migrationBuilder.DropTable(
                name: "LfsObjects");

            migrationBuilder.DropTable(
                name: "MergeQueueEntries");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PackageDistTags");

            migrationBuilder.DropTable(
                name: "PackageFiles");

            migrationBuilder.DropTable(
                name: "PagesDeployments");

            migrationBuilder.DropTable(
                name: "PersonalAccessTokens");

            migrationBuilder.DropTable(
                name: "PipelineArtifacts");

            migrationBuilder.DropTable(
                name: "PipelineStepLogs");

            migrationBuilder.DropTable(
                name: "ProjectEditorSettings");

            migrationBuilder.DropTable(
                name: "ProjectMembers");

            migrationBuilder.DropTable(
                name: "ProjectPackageSettings");

            migrationBuilder.DropTable(
                name: "ProjectStars");

            migrationBuilder.DropTable(
                name: "ProtectedTagRules");

            migrationBuilder.DropTable(
                name: "PullRequestActivities");

            migrationBuilder.DropTable(
                name: "PullRequestComments");

            migrationBuilder.DropTable(
                name: "PullRequestReviewers");

            migrationBuilder.DropTable(
                name: "PullRequestReviews");

            migrationBuilder.DropTable(
                name: "PushEvents");

            migrationBuilder.DropTable(
                name: "RegistrationInvites");

            migrationBuilder.DropTable(
                name: "ReleaseAssets");

            migrationBuilder.DropTable(
                name: "RepositoryAiReviewSettings");

            migrationBuilder.DropTable(
                name: "RepositoryImports");

            migrationBuilder.DropTable(
                name: "RepositoryMirrors");

            migrationBuilder.DropTable(
                name: "RepositoryRedirects");

            migrationBuilder.DropTable(
                name: "RepositorySecuritySettings");

            migrationBuilder.DropTable(
                name: "SavedFilterStars");

            migrationBuilder.DropTable(
                name: "SecurityFindings");

            migrationBuilder.DropTable(
                name: "SecurityTriages");

            migrationBuilder.DropTable(
                name: "ServiceDeskMessages");

            migrationBuilder.DropTable(
                name: "ServiceDeskSettings");

            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropTable(
                name: "SnippetComments");

            migrationBuilder.DropTable(
                name: "SnippetFiles");

            migrationBuilder.DropTable(
                name: "SshKeys");

            migrationBuilder.DropTable(
                name: "TrackerImportJobs");

            migrationBuilder.DropTable(
                name: "UserNotificationSettings");

            migrationBuilder.DropTable(
                name: "Watches");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "WikiPageIndex");

            migrationBuilder.DropTable(
                name: "WorkbenchConnections");

            migrationBuilder.DropTable(
                name: "WorkbenchIssueLinks");

            migrationBuilder.DropTable(
                name: "WorkbenchOutbox");

            migrationBuilder.DropTable(
                name: "WorkbenchPlanSyncs");

            migrationBuilder.DropTable(
                name: "WorkbenchTaskSyncs");

            migrationBuilder.DropTable(
                name: "WorkflowTransitions");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropTable(
                name: "Boards");

            migrationBuilder.DropTable(
                name: "CustomFields");

            migrationBuilder.DropTable(
                name: "Dashboards");

            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "EditorWorkspaces");

            migrationBuilder.DropTable(
                name: "FeatureFlags");

            migrationBuilder.DropTable(
                name: "FlagEnvironments");

            migrationBuilder.DropTable(
                name: "ProjectComponents");

            migrationBuilder.DropTable(
                name: "Labels");

            migrationBuilder.DropTable(
                name: "IssueTriageRuns");

            migrationBuilder.DropTable(
                name: "ProjectVersions");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "PagesSites");

            migrationBuilder.DropTable(
                name: "PipelineSteps");

            migrationBuilder.DropTable(
                name: "Releases");

            migrationBuilder.DropTable(
                name: "SavedFilters");

            migrationBuilder.DropTable(
                name: "SecurityScans");

            migrationBuilder.DropTable(
                name: "ServiceDeskTickets");

            migrationBuilder.DropTable(
                name: "SnippetRevisions");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropTable(
                name: "CiEnvironments");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropTable(
                name: "PipelineJobs");

            migrationBuilder.DropTable(
                name: "Issues");

            migrationBuilder.DropTable(
                name: "ServiceDeskContacts");

            migrationBuilder.DropTable(
                name: "Snippets");

            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropTable(
                name: "Milestones");

            migrationBuilder.DropTable(
                name: "Sprints");

            migrationBuilder.DropTable(
                name: "WorkflowStatuses");

            migrationBuilder.DropTable(
                name: "PipelineSchedules");

            migrationBuilder.DropTable(
                name: "PullRequests");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Groups");
        }
    }
}
