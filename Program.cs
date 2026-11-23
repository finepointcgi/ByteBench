using ByteBench.Components;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Ai;
using ByteBench.Services.Ai.Triage;
using ByteBench.Services.Ci;
using ByteBench.Services.Webhooks;
using ByteBench.Api;
using ByteBench.Services.Audit;
using ByteBench.Services.Releases;
using ByteBench.Services.Hosting;
using ByteBench.Services.Ssh;
using ByteBench.Services.Automation;
using ByteBench.Services.Query;
using ByteBench.Services.Sso;
using ByteBench.Services.Fields;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;

// `dotnet ByteBench.dll backup|restore ...` run before (instead of) the web host.
if (ByteBench.Services.Hosting.Backup.BackupCli.IsCommand(args)) return await ByteBench.Services.Hosting.Backup.BackupCli.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);
builder.AddByteBenchHosting();
ByteBench.Services.Hosting.Backup.BackupServiceCollectionExtensions.AddBackups(builder.Services, builder.Configuration); // backup/restore, scheduled backups, /admin/system, retention jobs // data protection keys, forwarded headers, cookies, rate limits, health checks

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices(); // MudBlazor UI components (dialogs, snackbars, popovers)

builder.Services.AddByteBenchDatabase(builder.Configuration); // Database:Provider = Sqlite (default) | Postgres

// ----- Authentication (ASP.NET Core Identity, cookie-based) -----
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthorization();
builder.Services.AddIdentityCore<User>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.AccessDeniedPath = "/account/login";
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
});
builder.AddByteBenchSso(); // SSO (OIDC providers, GitHub OAuth) and LDAP from Authentication:*

builder.Services.AddScoped<IssueService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ProjectAccess>();
builder.Services.AddScoped<MemberService>();
builder.Services.AddScoped<GroupService>(); // groups: namespaces, inherited roles, group labels/milestones
builder.Services.AddScoped<AccessTokenService>();
builder.Services.AddSingleton<RepoStorage>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.Configure<ByteBench.Services.Workbench.WorkbenchOptions>(builder.Configuration.GetSection("Workbench")); // optional link to Workbench timesheets
builder.Services.AddSingleton<AiClient>();
builder.Services.AddScoped<IssueAiService>();
builder.Services.AddAiCodeReview(builder.Configuration);
builder.Services.AddAiTriage(builder.Configuration);
builder.Services.AddScoped<RepositoryService>();
builder.Services.AddScoped<PullRequestService>();
builder.Services.AddScoped<IGitPushListener, IssueCommitPushListener>();
builder.Services.AddSingleton<LiveEvents>();
builder.Services.AddScoped<NotificationService>();
// Branch protection, merge checks & assigned reviewers
builder.Services.AddScoped<BranchProtectionService>();
builder.Services.AddScoped<MergeGate>();
builder.Services.AddScoped<ReviewerService>();
builder.Services.AddScoped<CodeOwnersService>(); // PR features: CODEOWNERS
builder.Services.AddScoped<IGitPushListener, StaleApprovalPushListener>();
ByteBench.Services.MergeQueue.MergeQueueServiceCollectionExtensions.AddMergeQueue(builder.Services, builder.Configuration); // merge queues on protected branches
builder.Services.AddCiPipelines(builder.Configuration);
ByteBench.Services.Security.SecurityServiceCollectionExtensions.AddSecurityScanning(builder.Services, builder.Configuration); // Night Owl reports, Security page, merge check
builder.Services.AddScoped<RepositorySettingsService>(); // code tools: repo settings, rename, archive, delete
builder.Services.AddScoped<ByteBench.Services.Code.CodeSearchService>();
ByteBench.Services.Code.BlameServiceCollectionExtensions.AddBlame(builder.Services, builder.Configuration); // blame view + API
builder.Services.AddWebhooks(builder.Configuration);
ByteBench.Services.ChatIntegrations.ChatServiceCollectionExtensions.AddChatIntegrations(builder.Services, builder.Configuration); // Slack & Teams (on the webhook pipeline), Slack app
builder.Services.AddScoped<LinkedWorkService>(); // linked work: sub-tasks, epics, issue links
builder.Services.AddByteBenchApi();
// Attachments, saved filters & bulk edit
builder.Services.AddSingleton<ByteBench.Services.Attachments.AttachmentStorage>();
builder.Services.AddScoped<ByteBench.Services.Attachments.AttachmentService>();
builder.Services.AddScoped<SavedFilterService>();
builder.Services.AddScoped<SprintService>(); // sprint planning, backlog, burndown & velocity
builder.Services.AddScoped<ByteBench.Services.Roadmap.RoadmapService>(); // roadmap timeline
builder.Services.AddScoped<ByteBench.Services.Dashboards.DashboardService>(); // dashboards & gadgets
builder.Services.AddScoped<ByteBench.Services.Dashboards.GadgetDataService>();
builder.Services.AddAuditLog(builder.Configuration); // audit log + retention
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<ByteBench.Services.Boards.BoardService>(); // multiple configurable boards per project
ByteBench.Services.Email.EmailServiceCollectionExtensions.AddEmail(builder.Services, builder.Configuration, builder.Environment); // email, preferences, autocomplete
// ----- Admin, 2FA & CI secrets -----
builder.Services.AddScoped<AdminUserService>(); // /admin/users
builder.Services.AddScoped<CiVariableService>(); // CI secret variables (Data Protection)
// Re-check cookies against the security stamp every minute, so an admin's "sign out" or lock takes effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddReleases(); // tags, releases, assets, source archives
// ----- Explore & profiles -----
builder.Services.AddScoped<ByteBench.Services.Explore.StarService>();
builder.Services.AddScoped<ByteBench.Services.Explore.ExploreService>();
builder.Services.AddScoped<ByteBench.Services.Explore.UserActivityService>();
builder.Services.AddScoped<ByteBench.Services.Explore.SearchService>();
builder.Services.AddScoped<ByteBench.Services.Wiki.WikiService>(); // project wikis (git-backed) and snippets
builder.Services.AddScoped<ByteBench.Services.Snippets.SnippetService>();
builder.Services.AddScoped<ByteBench.Services.Git.IGitPushListener, ByteBench.Services.Explore.PushActivityListener>();
builder.Services.AddSshGit(builder.Configuration); // SSH keys, deploy keys, git over SSH (Ssh:Enabled)
// ----- Mirroring, protected tags & Git LFS -----
builder.Services.AddScoped<ProtectedTagService>();
ByteBench.Services.Mirroring.MirroringServiceCollectionExtensions.AddMirroring(builder.Services, builder.Configuration);
ByteBench.Services.Importing.ImportServiceCollectionExtensions.AddRepositoryImport(builder.Services, builder.Configuration); // repository import wizard
ByteBench.Services.Lfs.LfsServiceCollectionExtensions.AddGitLfs(builder.Services);
builder.Services.AddAutomation(builder.Configuration); // automation rules: triggers, conditions, actions, scheduler
builder.Services.AddByteBenchQuery(builder.Configuration); // GQL advanced search, CSV export and import
// ----- Forks & cross-fork pull requests -----
builder.Services.AddScoped<ByteBench.Services.Forks.ForkService>();
builder.Services.AddScoped<ByteBench.Services.Forks.ForkPullRequestSync>();
builder.Services.AddScoped<IGitPushListener, ByteBench.Services.Forks.ForkPushListener>();
ByteBench.Services.Reports.ReportsServiceCollectionExtensions.AddDeliveryReports(builder.Services); // cycle/lead time, flow, DORA and pull request reports
builder.Services.AddIssueFields(); // custom fields, components, versions, issue templates, estimates, Workbench link
ByteBench.Services.Editor.EditorServiceCollectionExtensions.AddWebEditor(builder.Services, builder.Configuration); // web editor + AI assistant
ByteBench.Services.Packages.PackagesServiceCollectionExtensions.AddPackageRegistry(builder.Services); // NuGet, npm and container registry per project
ByteBench.Services.ServiceDesk.ServiceDeskServiceCollectionExtensions.AddServiceDesk(builder.Services, builder.Configuration); // service desk: inbound mail, public form, queue
ByteBench.Services.Pages.PagesServiceCollectionExtensions.AddPages(builder.Services, builder.Configuration); // static sites from CI output (Pages:*)

ByteBench.Services.Flags.FlagsServiceCollectionExtensions.AddFeatureFlags(builder.Services); // feature flags per project, SDK keys, live updates

var app = builder.Build();
app.UseByteBenchForwardedHeaders(); // first: everything after sees the client's IP and scheme
ProductionChecks.LogAtStartup(app.Configuration, app.Environment, app.Logger); // hosting review: warnings in the log

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    if (ProductionSetup.SeedDemoData(app)) SeedData.Initialize(db); // demo users, projects and issues (Development by default)
    else db.Database.Migrate();
    ProductionSetup.PromoteBootstrapAdmins(db, app.Configuration, app.Logger);
    RepoSeeder.Initialize(db, scope.ServiceProvider.GetRequiredService<RepoStorage>());
    NotificationService.Initialize(db);
    RepoSeeder.InitializeOps(db, scope.ServiceProvider.GetRequiredService<RepoStorage>());
    SprintSeeder.Initialize(db);
    FieldsSeeder.Initialize(db); // WEB: custom fields, components, versions, a template and estimates
    WorkflowService.EnsureDefaults(db); // default workflow for projects without one; maps issues to workflow statuses
    scope.ServiceProvider.GetRequiredService<WebhookSecrets>().EncryptLegacy(db); // webhook secrets stored before encryption
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseByteBenchHttps(); // HSTS + HTTPS redirection outside Development
app.UseByteBenchSecurityHeaders(); // CSP, X-Frame-Options, Referrer-Policy, …
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Explicit (rather than the implicit ones WebApplication adds first) so they also run when
// UseStatusCodePagesWithReExecute/UseExceptionHandler re-execute into /not-found or /Error,
// which carry authorization metadata ([AllowAnonymous]).
app.UseAuthentication();
// Pages serves /pages/... and Pages:BaseDomain hosts only. It sits before UseAuthorization, which would otherwise challenge the
// app's own endpoints that a site's "/" matches (Home); it does its own access checks. Before rate limiting, antiforgery and static assets.
ByteBench.Services.Pages.PagesServiceCollectionExtensions.UsePages(app);
app.UseAuthorization();
app.UseRateLimiter(); // after authentication, so API limits can key on the signed-in user

app.UseAntiforgery();

app.MapStaticAssets();
app.UseWebhookPushCapture();
app.MapGitHttp();
ByteBench.Services.Lfs.LfsEndpoints.MapGitLfs(app); // /git/{KEY}/{slug}.git/info/lfs/...
ByteBench.Services.Packages.PackagesServiceCollectionExtensions.MapPackageRegistry(app); // /packages/{KEY}/nuget|npm/..., /v2/ (OCI)
app.MapCiEndpoints();
ByteBench.Services.Security.SecurityServiceCollectionExtensions.MapSecurityDownloads(app); // triage.json export
ByteBench.Services.Flags.FlagSdkEndpoints.MapFlagSdk(app); // /api/v1/flags/*: SDK-key authenticated evaluation (before the user API's catch-all)
app.MapByteBenchApi();
ByteBench.Services.ChatIntegrations.SlackEndpoints.MapSlackIntegration(app); // /integrations/slack/{commands,interactivity,events}
ByteBench.Services.Attachments.AttachmentDownloads.MapAttachmentDownloads(app);
ByteBench.Services.Wiki.WikiWebEndpoints.MapWikiAndSnippetDownloads(app); // /wiki-files, snippet raw & embed
app.MapAuditExport();
app.MapIssueCsvExport(); // /p/{key}/issues/export.csv, /issues/export.csv
ByteBench.Services.Reports.ReportsServiceCollectionExtensions.MapReportDownloads(app); // /p/{key}/reports/{report}.csv
ByteBench.Services.Email.UnsubscribeEndpoints.MapEmailUnsubscribe(app);
ByteBench.Services.ServiceDesk.ServiceDeskServiceCollectionExtensions.MapServiceDesk(app); // POST /service-desk/inbound (signed raw MIME)
app.MapReleases();
app.MapByteBenchHealth(); // /health/live, /health/ready
ByteBench.Services.Hosting.Backup.BackupServiceCollectionExtensions.MapBackupDownloads(app); // /admin/backups/download/{name}
app.MapByteBenchSso(); // /account/sso/*: start, complete and link SSO sign-ins
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Sign-out is a POST (with antiforgery token, enforced because of the [FromForm] binding).
app.MapPost("/account/logout", async (SignInManager<User> signIn, AuditLog audit, [FromForm] string? returnUrl) =>
{
    await audit.RecordAsync("auth.sign_out");
    await signIn.SignOutAsync();
    return TypedResults.LocalRedirect("/account/login");
});

app.Run();
return 0;

// Lets the integration tests (tests/ByteBench.Tests) use WebApplicationFactory<Program>.
public partial class Program;
