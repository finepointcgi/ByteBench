using GitBench.Components;
using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Services.Ai;
using GitBench.Services.Ci;
using GitBench.Services.Webhooks;
using GitBench.Api;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

builder.Services.AddScoped<IssueService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ProjectAccess>();
builder.Services.AddScoped<MemberService>();
builder.Services.AddScoped<AccessTokenService>();
builder.Services.AddSingleton<RepoStorage>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    SeedData.Initialize(db);
    RepoSeeder.Initialize(db, scope.ServiceProvider.GetRequiredService<RepoStorage>());
    NotificationService.Initialize(db);
    RepoSeeder.InitializeOps(db, scope.ServiceProvider.GetRequiredService<RepoStorage>());
    SprintSeeder.Initialize(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Explicit (rather than the implicit ones WebApplication adds first) so they also run when
// UseStatusCodePagesWithReExecute/UseExceptionHandler re-execute into /not-found or /Error,
// which carry authorization metadata ([AllowAnonymous]).
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.UseWebhookPushCapture();
app.MapGitHttp();
app.MapCiEndpoints();
app.MapGitBenchApi();
GitBench.Services.Attachments.AttachmentDownloads.MapAttachmentDownloads(app);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Sign-out is a POST (with antiforgery token, enforced because of the [FromForm] binding).
app.MapPost("/account/logout", async (SignInManager<User> signIn, [FromForm] string? returnUrl) =>
{
    await signIn.SignOutAsync();
    return TypedResults.LocalRedirect("/account/login");
});

app.Run();
