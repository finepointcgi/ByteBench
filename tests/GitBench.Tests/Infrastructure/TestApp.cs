using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitBench.Tests.Infrastructure;

/// <summary>
/// The whole app (Program.cs, migrations, demo seed) on a throw-away directory: its own SQLite database, bare repos and
/// attachments under a temp folder, AI and CI turned off. One instance per test class (<c>IClassFixture</c>), so test
/// classes run in parallel without sharing state. Tests should create their own projects where they mutate data.
/// </summary>
public class TestApp : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "gitbench-tests", Guid.NewGuid().ToString("N"));
    public string RepoRoot => Path.Combine(DataRoot, "repos");
    public string AttachmentRoot => Path.Combine(DataRoot, "attachments");

    public TestApp() => Directory.CreateDirectory(DataRoot);

    /// <summary>Extra configuration for a derived fixture (e.g. turning CI back on).</summary>
    protected virtual void AddSettings(IDictionary<string, string?> settings) { }

    /// <summary>Service replacements for a derived fixture (e.g. a fake AI model), applied after Program.cs.</summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(DataRoot, "gitbench.db")}",
            ["Git:RepositoryRoot"] = RepoRoot,
            ["Attachments:Root"] = AttachmentRoot,
            ["Ai:Enabled"] = "false",
            ["Ai:ApiKey"] = "",
            // The web editor's AI: never the real Claude Code on this machine (WebEditorClaudeCodeTests uses a fake one).
            ["Ai:Editor:Backend"] = "Api",
            ["Ai:ClaudeCode:Path"] = Path.Combine(DataRoot, "no-claude-here"),
            ["Ci:Enabled"] = "false",
            ["Ci:WorkDirectory"] = Path.Combine(DataRoot, "ci"),
            ["Ci:ArtifactsRoot"] = Path.Combine(DataRoot, "ci-artifacts"),
            ["Ci:CacheRoot"] = Path.Combine(DataRoot, "ci-cache"),
            ["Webhooks:AllowPrivateNetworks"] = "true",
            ["Ssh:Enabled"] = "false", // SshTests turns it on with a free port
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:GitBench.GitHttp"] = "Warning",
        };
        AddSettings(settings);
        builder.UseEnvironment("Development");
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(ConfigureServices);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try { DeleteDirectory(DataRoot); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal); // git marks objects read-only
        Directory.Delete(dir, recursive: true);
    }

    // ---------- Services ----------

    /// <summary>A DI scope whose <see cref="ProjectAccess"/> acts as <paramref name="actAs"/> (or the system when null and <paramref name="system"/>).</summary>
    public TestScope Scope(int? actAs = null, bool system = false)
    {
        var scope = Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
        if (actAs is { } id) access.ActAs(id);
        else if (system) access.ActAsSystem();
        return new TestScope(scope);
    }

    public AppDbContext Db() => Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();

    public int UserId(string userName)
    {
        using var db = Db();
        return db.Users.Where(u => u.UserName == userName).Select(u => u.Id).First();
    }

    public User User(string userName)
    {
        using var db = Db();
        return db.Users.AsNoTracking().First(u => u.UserName == userName);
    }

    public int ProjectId(string key)
    {
        using var db = Db();
        return db.Projects.Where(p => p.Key == key).Select(p => p.Id).First();
    }

    /// <summary>A new site user (not a member of anything) with the demo password.</summary>
    public async Task<User> CreateUserAsync(string userName)
    {
        await using var s = Scope();
        var users = s.Get<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var user = new User { UserName = userName, Email = userName + "@example.com", DisplayName = userName, Color = "#123456", EmailConfirmed = true };
        var result = await users.CreateAsync(user, SeedData.DemoPassword);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    private static int projectCounter;

    /// <summary>A fresh project owned by <paramref name="owner"/>, plus members. Keys are unique per fixture.</summary>
    public async Task<Project> CreateProjectAsync(string owner = "alice", ProjectVisibility visibility = ProjectVisibility.Private,
        params (string User, ProjectRole Role)[] members)
    {
        var key = "T" + Interlocked.Increment(ref projectCounter).ToString("D3");
        await using var s = Scope(UserId(owner));
        var project = await s.Get<IssueService>().CreateProjectAsync(new Project { Key = key, Name = "Test " + key, Visibility = visibility });
        foreach (var (user, role) in members)
            await s.Get<MemberService>().AddMemberAsync(project.Id, user, role);
        return project;
    }

    public async Task<Issue> CreateIssueAsync(int projectId, string actor, string title = "An issue", IssueType type = IssueType.Task,
        int? parentId = null, int? storyPoints = null, int? sprintId = null)
    {
        var actorId = UserId(actor);
        await using var s = Scope(actorId);
        return await s.Get<IssueService>().CreateIssueAsync(projectId,
            new Issue { Title = title, Type = type, ParentId = parentId, StoryPoints = storyPoints, SprintId = sprintId }, [], actorId);
    }

    public async Task<Repository> CreateRepoAsync(int projectId, string actor = "alice", string name = "code")
    {
        await using var s = Scope(UserId(actor));
        var repo = await s.Get<RepositoryService>().CreateAsync(projectId, name, null, null, User(actor));
        return (await s.Get<RepositoryService>().GetRepositoryAsync(ProjectKey(projectId), repo.Slug))!;
    }

    /// <summary>Commits files straight into the bare repo (creating <paramref name="branch"/> from <paramref name="from"/> if needed).</summary>
    public string Commit(Repository repo, string branch, string file, string content, string? from = "main", string message = "change")
    {
        var path = Services.GetRequiredService<RepoStorage>().PathFor(ProjectKey(repo.ProjectId), repo.Slug);
        return GitBench.Services.Git.GitOps.CommitFiles(path, branch, new Dictionary<string, string?> { [file] = content }, message,
            new GitBench.Services.Git.GitIdentity("Test", "test@example.com"), DateTimeOffset.UtcNow, from);
    }

    public string ProjectKey(int projectId)
    {
        using var db = Db();
        return db.Projects.Where(p => p.Id == projectId).Select(p => p.Key).First();
    }

    public async Task<string> CreateTokenAsync(string user, TokenScopes scopes, DateTime? expires = null)
    {
        await using var s = Scope();
        var (_, plaintext) = await s.Get<AccessTokenService>().CreateAsync(UserId(user), "test " + Guid.NewGuid().ToString("N")[..6], scopes, expires);
        return plaintext;
    }

    /// <summary>An HTTP client that authenticates to the API with a fresh token for <paramref name="user"/>.</summary>
    public async Task<HttpClient> ApiClientAsync(string user, TokenScopes scopes = TokenScopes.ReadApi | TokenScopes.WriteApi)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await CreateTokenAsync(user, scopes));
        return client;
    }

    /// <summary>A client signed in with the browser cookie, through the real login form.</summary>
    public async Task<HttpClient> CookieClientAsync(string user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await client.GetStringAsync("/account/login");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.UserNameOrEmail"] = user,
            ["Input.Password"] = SeedData.DemoPassword,
        }));
        if ((int)response.StatusCode is not (302 or 303))
            throw new InvalidOperationException($"Sign-in as {user} failed: {(int)response.StatusCode}");
        return client;
    }

    /// <summary>The address Kestrel listens on (only for fixtures that called <c>UseKestrel</c>).</summary>
    public Uri ServerAddress =>
        new(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
}

/// <summary>A DI scope with a short <see cref="Get{T}"/>.</summary>
public sealed class TestScope(AsyncServiceScope scope) : IAsyncDisposable
{
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
