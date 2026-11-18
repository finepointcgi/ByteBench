using GitBench.Components;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Services.Ai;
using GitBench.Services.Ci;
using GitBench.Services.Webhooks;
using GitBench.Api;
using GitBench.Services.Audit;
using GitBench.Services.Releases;
using GitBench.Services.Hosting;
using GitBench.Services.Ssh;
using GitBench.Services.Automation;
using GitBench.Services.Query;
using GitBench.Services.Sso;
using GitBench.Services.Fields;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddGitBenchHosting(); // data protection keys, forwarded headers, cookies, rate limits, health checks

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices(); // MudBlazor UI components (dialogs, snackbars, popovers)

builder.Services.AddDbContextFactory<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=gitbench.db"));

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
builder.AddGitBenchSso(); // SSO (OIDC providers, GitHub OAuth) and LDAP from Authentication:*

builder.Services.AddScoped<IssueService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ProjectAccess>();
builder.Services.AddScoped<MemberService>();
builder.Services.AddScoped<GroupService>(); // groups: namespaces, inherited roles, group labels/milestones
builder.Services.AddScoped<AccessTokenService>();
builder.Services.AddSingleton<RepoStorage>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.Configure<GitBench.Services.Workbench.WorkbenchOptions>(builder.Configuration.GetSection("Workbench")); // optional link to Workbench timesheets
builder.Services.AddSingleton<AiClient>();
builder.Services.AddScoped<IssueAiService>();
builder.Services.AddAiCodeReview();
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
builder.Services.AddCiPipelines(builder.Configuration);
builder.Services.AddScoped<RepositorySettingsService>(); // code tools: repo settings, rename, archive, delete
builder.Services.AddScoped<GitBench.Services.Code.CodeSearchService>();
builder.Services.AddWebhooks(builder.Configuration);
builder.Services.AddScoped<LinkedWorkService>(); // linked work: sub-tasks, epics, issue links
builder.Services.AddGitBenchApi();
// Attachments, saved filters & bulk edit
builder.Services.AddSingleton<GitBench.Services.Attachments.AttachmentStorage>();
builder.Services.AddScoped<GitBench.Services.Attachments.AttachmentService>();
builder.Services.AddScoped<SavedFilterService>();
builder.Services.AddScoped<SprintService>(); // sprint planning, backlog, burndown & velocity
builder.Services.AddScoped<GitBench.Services.Roadmap.RoadmapService>(); // roadmap timeline
builder.Services.AddScoped<GitBench.Services.Dashboards.DashboardService>(); // dashboards & gadgets
builder.Services.AddScoped<GitBench.Services.Dashboards.GadgetDataService>();
builder.Services.AddAuditLog(builder.Configuration); // audit log + retention
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<GitBench.Services.Boards.BoardService>(); // multiple configurable boards per project
GitBench.Services.Email.EmailServiceCollectionExtensions.AddEmail(builder.Services, builder.Configuration, builder.Environment); // email, preferences, autocomplete
// ----- Admin, 2FA & CI secrets -----
builder.Services.AddScoped<AdminUserService>(); // /admin/users
builder.Services.AddScoped<CiVariableService>(); // CI secret variables (Data Protection)
// Re-check cookies against the security stamp every minute, so an admin's "sign out" or lock takes effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddReleases(); // tags, releases, assets, source archives
// ----- Explore & profiles -----
builder.Services.AddScoped<GitBench.Services.Explore.StarService>();
builder.Services.AddScoped<GitBench.Services.Explore.ExploreService>();
builder.Services.AddScoped<GitBench.Services.Explore.UserActivityService>();
builder.Services.AddScoped<GitBench.Services.Explore.SearchService>();
builder.Services.AddScoped<GitBench.Services.Wiki.WikiService>(); // project wikis (git-backed) and snippets
builder.Services.AddScoped<GitBench.Services.Snippets.SnippetService>();
builder.Services.AddScoped<GitBench.Services.Git.IGitPushListener, GitBench.Services.Explore.PushActivityListener>();
builder.Services.AddSshGit(builder.Configuration); // SSH keys, deploy keys, git over SSH (Ssh:Enabled)
// ----- Mirroring, protected tags & Git LFS -----
builder.Services.AddScoped<ProtectedTagService>();
GitBench.Services.Mirroring.MirroringServiceCollectionExtensions.AddMirroring(builder.Services, builder.Configuration);
GitBench.Services.Lfs.LfsServiceCollectionExtensions.AddGitLfs(builder.Services);
builder.Services.AddAutomation(builder.Configuration); // automation rules: triggers, conditions, actions, scheduler
builder.Services.AddGitBenchQuery(builder.Configuration); // GQL advanced search, CSV export and import
// ----- Forks & cross-fork pull requests -----
builder.Services.AddScoped<GitBench.Services.Forks.ForkService>();
builder.Services.AddScoped<GitBench.Services.Forks.ForkPullRequestSync>();
builder.Services.AddScoped<IGitPushListener, GitBench.Services.Forks.ForkPushListener>();
builder.Services.AddIssueFields(); // custom fields, components, versions, issue templates, estimates, Workbench link

var app = builder.Build();
app.UseGitBenchForwardedHeaders(); // first: everything after sees the client's IP and scheme

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
app.UseGitBenchHttps(); // HSTS + HTTPS redirection outside Development
app.UseGitBenchSecurityHeaders(); // CSP, X-Frame-Options, Referrer-Policy, …
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Explicit (rather than the implicit ones WebApplication adds first) so they also run when
// UseStatusCodePagesWithReExecute/UseExceptionHandler re-execute into /not-found or /Error,
// which carry authorization metadata ([AllowAnonymous]).
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after authentication, so API limits can key on the signed-in user

app.UseAntiforgery();

app.MapStaticAssets();
app.UseWebhookPushCapture();
app.MapGitHttp();
GitBench.Services.Lfs.LfsEndpoints.MapGitLfs(app); // /git/{KEY}/{slug}.git/info/lfs/...
app.MapCiEndpoints();
app.MapGitBenchApi();
GitBench.Services.Attachments.AttachmentDownloads.MapAttachmentDownloads(app);
GitBench.Services.Wiki.WikiWebEndpoints.MapWikiAndSnippetDownloads(app); // /wiki-files, snippet raw & embed
app.MapAuditExport();
app.MapIssueCsvExport(); // /p/{key}/issues/export.csv, /issues/export.csv
GitBench.Services.Email.UnsubscribeEndpoints.MapEmailUnsubscribe(app);
app.MapReleases();
app.MapGitBenchHealth(); // /health/live, /health/ready
app.MapGitBenchSso(); // /account/sso/*: start, complete and link SSO sign-ins
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

// Lets the integration tests (tests/GitBench.Tests) use WebApplicationFactory<Program>.
public partial class Program;
