using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Audit;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.DependencyBot;

/// <summary>A configured private registry as the settings page sees it. There is no credential here, only whether one is set.</summary>
public sealed record RegistryView(int Id, string Ecosystem, string Scope, string Url, DependencyRegistryAuth AuthKind, bool HasCredential, string? ExtraOrigins, int SortOrder);

/// <summary>What the registry form edits. <see cref="Secret"/> (and <see cref="Username"/>) are write-only: leave the secret empty to keep the stored credential.</summary>
public sealed class RegistryInput
{
    public int? Id { get; set; }
    public string Ecosystem { get; set; } = Ecosystems.Npm;
    /// <summary>npm: empty for the default registry or <c>@scope</c>. Ignored by the other ecosystems and when editing.</summary>
    public string? Scope { get; set; }
    public string Url { get; set; } = "";
    public DependencyRegistryAuth AuthKind { get; set; }
    public string? Username { get; set; }
    public string? Secret { get; set; }
    /// <summary>NuGet only: additional origins a service index may name (comma separated).</summary>
    public string? ExtraOrigins { get; set; }
}

public sealed record RegistryTestResult(bool Ok, string Message);

/// <summary>A registry address found in the repository's own configuration files (never a credential).</summary>
public sealed record DetectedRegistry(string Ecosystem, string Scope, string Url, string Source);

/// <summary>
/// The permission-checked face of the private registries on the dependency settings page (everything needs
/// <see cref="Permission.ManageRepositories"/>, Maintainer and Owner). Credentials are encrypted on the way in and never come back out.
/// </summary>
public sealed partial class DependencyRegistryService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, AuditLog audit, RegistryCredentialProtector protector,
    RegistryUrlGuard guard, RegistryHttp http, RegistryClient client, RepoStorage storage)
{
    public const int MaxRegistries = 50;
    public const int MaxCredentialLength = 2000;

    public bool AllowPrivateNetworks => guard.AllowPrivateNetworks;

    /// <summary>The address rules for a private registry: the SSRF guard, and https (plain http only for a loopback address while private networks are allowed).</summary>
    public string? ValidateUrl(string? url)
    {
        if (guard.Validate(url) is { } invalid) return invalid;
        var uri = new Uri(url!.Trim());
        if (uri.Scheme != Uri.UriSchemeHttps && !(RegistryOrigin.IsLoopback(uri) && guard.AllowPrivateNetworks))
            return "A private registry must use https:// (plain http is only accepted for a loopback address while DependencyBot:AllowPrivateNetworks is on).";
        return null;
    }

    public async Task<List<RegistryView>> ListAsync(Repository repo)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.DependencyBotRegistries.AsNoTracking().Where(r => r.RepositoryId == repo.Id).ToListAsync())
            .OrderBy(r => Array.IndexOf(Ecosystems.All, r.Ecosystem)).ThenBy(r => r.Scope.Length == 0 ? 0 : 1).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .Select(View).ToList();
    }

    private static RegistryView View(DependencyBotRegistry r) =>
        new(r.Id, r.Ecosystem, r.Scope, r.Url, r.AuthKind, !string.IsNullOrEmpty(r.EncryptedCredential), r.ExtraOrigins, r.SortOrder);

    [GeneratedRegex(@"^@[a-z0-9][a-z0-9._~-]{0,99}$")]
    private static partial Regex NpmScope();

    private static bool BadText(string s) => s.Any(c => char.IsControl(c));

    public async Task<RegistryView> SaveAsync(Repository repo, RegistryInput input, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var ecosystem = (input.Ecosystem ?? "").Trim().ToLowerInvariant();
        if (!Ecosystems.All.Contains(ecosystem)) throw new InvalidOperationException("Pick an ecosystem (npm, nuget, pip or go).");
        var url = (input.Url ?? "").Trim();
        if (ValidateUrl(url) is { } urlError) throw new InvalidOperationException(urlError);

        string? extra = null;
        if (ecosystem == Ecosystems.NuGet && !string.IsNullOrWhiteSpace(input.ExtraOrigins))
        {
            var origins = new List<string>();
            foreach (var part in input.ExtraOrigins.Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ValidateUrl(part) is { } e) throw new InvalidOperationException($"Additional origin '{Trunc(part)}': {e}");
                origins.Add(RegistryOrigin.Of(part)!);
            }
            if (origins.Count > 5) throw new InvalidOperationException("At most 5 additional origins.");
            extra = origins.Count == 0 ? null : string.Join(",", origins.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        var secret = string.IsNullOrEmpty(input.Secret) ? null : input.Secret;
        var user = string.IsNullOrEmpty(input.Username) ? null : input.Username;
        if (secret != null && (secret.Length > MaxCredentialLength || BadText(secret))) throw new InvalidOperationException("The credential is too long or contains control characters.");
        if (user != null && (user.Length > 500 || BadText(user) || user.Contains(':'))) throw new InvalidOperationException("The user name is too long, contains control characters or a colon.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var mine = await db.DependencyBotRegistries.Where(r => r.RepositoryId == repo.Id).ToListAsync();
        DependencyBotRegistry? row = null;
        if (input.Id is { } id)
        {
            row = mine.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("That registry no longer exists.");
            if (row.Ecosystem != ecosystem) throw new InvalidOperationException("The ecosystem of a registry can't be changed; remove it and add another.");
        }
        else
        {
            if (mine.Count >= MaxRegistries) throw new InvalidOperationException($"At most {MaxRegistries} registries per repository.");
            var scope = "";
            if (ecosystem == Ecosystems.Npm)
            {
                scope = (input.Scope ?? "").Trim().ToLowerInvariant();
                if (scope.Length > 0 && !NpmScope().IsMatch(scope)) throw new InvalidOperationException("An npm scope looks like @acme.");
            }
            else if (ecosystem == Ecosystems.NuGet)
            {
                var n = mine.Where(r => r.Ecosystem == ecosystem).Select(r => r.Scope.StartsWith("source-", StringComparison.Ordinal) && int.TryParse(r.Scope.AsSpan(7), out var x) ? x : 0).DefaultIfEmpty(0).Max() + 1;
                scope = "source-" + n;
            }
            if (mine.Any(r => r.Ecosystem == ecosystem && r.Scope == scope))
                throw new InvalidOperationException(scope.Length == 0 ? $"A default {Ecosystems.Label(ecosystem)} registry is already set; edit or remove it." : $"A registry for {scope} is already set; edit or remove it.");
            if (ecosystem == Ecosystems.NuGet && mine.Any(r => r.Ecosystem == ecosystem && r.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That NuGet source is already in the list.");
            row = new DependencyBotRegistry { RepositoryId = repo.Id, Ecosystem = ecosystem, Scope = scope };
            row.SortOrder = mine.Where(r => r.Ecosystem == ecosystem).Select(r => r.SortOrder).DefaultIfEmpty(0).Max() + 1;
            db.DependencyBotRegistries.Add(row);
        }

        var originChanged = row.Id != 0 && RegistryOrigin.Of(row.Url) != RegistryOrigin.Of(url);
        var credentialReplaced = false;
        if (input.AuthKind == DependencyRegistryAuth.None)
        {
            row.EncryptedCredential = row.EncryptedUsername = null;
        }
        else if (secret == null)
        {
            if (string.IsNullOrEmpty(row.EncryptedCredential) || row.AuthKind != input.AuthKind)
                throw new InvalidOperationException(input.AuthKind == DependencyRegistryAuth.Bearer ? "Enter the bearer token." : "Enter the user name and the password or token.");
            if (originChanged) throw new InvalidOperationException("The registry address changed to another host or port: enter the credential again (a stored credential is never moved to a new address).");
            if (user != null) throw new InvalidOperationException("To change the user name, enter the password or token again too.");
        }
        else
        {
            if (input.AuthKind == DependencyRegistryAuth.Basic && user == null) throw new InvalidOperationException("Basic authentication needs a user name.");
            row.EncryptedCredential = protector.ProtectCredential(secret);
            row.EncryptedUsername = input.AuthKind == DependencyRegistryAuth.Basic ? protector.ProtectUsername(user!) : null;
            credentialReplaced = true;
        }
        row.AuthKind = input.AuthKind;
        row.Url = url;
        row.ExtraOrigins = extra;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.dependency_registry_saved", "repository", repo.Id, repo.ProjectId,
            new { repository = repo.Slug, ecosystem, scope = row.Scope, host = RegistryOrigin.Of(url), auth = row.AuthKind.ToString().ToLowerInvariant(), credentialReplaced }, actorId);
        return View(row);
    }

    /// <summary>Explicitly removes the stored credential (the registry stays, without authentication).</summary>
    public async Task ClearCredentialAsync(Repository repo, int id, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.DependencyBotRegistries.FirstOrDefaultAsync(r => r.Id == id && r.RepositoryId == repo.Id) ?? throw new InvalidOperationException("That registry no longer exists.");
        row.AuthKind = DependencyRegistryAuth.None; row.EncryptedCredential = row.EncryptedUsername = null; row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.dependency_registry_credential_cleared", "repository", repo.Id, repo.ProjectId,
            new { repository = repo.Slug, ecosystem = row.Ecosystem, scope = row.Scope, host = RegistryOrigin.Of(row.Url) }, actorId);
    }

    public async Task RemoveAsync(Repository repo, int id, int? actorId = null)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.DependencyBotRegistries.FirstOrDefaultAsync(r => r.Id == id && r.RepositoryId == repo.Id) ?? throw new InvalidOperationException("That registry no longer exists.");
        db.DependencyBotRegistries.Remove(row);
        await db.SaveChangesAsync();
        await audit.RecordAsync("repository.dependency_registry_removed", "repository", repo.Id, repo.ProjectId,
            new { repository = repo.Slug, ecosystem = row.Ecosystem, scope = row.Scope, host = RegistryOrigin.Of(row.Url) }, actorId);
    }

    /// <summary>Moves a NuGet source up or down the list (the first source that knows a package wins).</summary>
    public async Task MoveAsync(Repository repo, int id, bool up)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.DependencyBotRegistries.FirstOrDefaultAsync(r => r.Id == id && r.RepositoryId == repo.Id) ?? throw new InvalidOperationException("That registry no longer exists.");
        var siblings = await db.DependencyBotRegistries.Where(r => r.RepositoryId == repo.Id && r.Ecosystem == row.Ecosystem).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync();
        var i = siblings.FindIndex(r => r.Id == id);
        var j = up ? i - 1 : i + 1;
        if (j < 0 || j >= siblings.Count) return;
        (siblings[i], siblings[j]) = (siblings[j], siblings[i]);
        for (var k = 0; k < siblings.Count; k++) siblings[k].SortOrder = k + 1;
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ test connection

    /// <summary>One light request to the stored registry with its stored credential; the answer is readable text and never shows the credential.</summary>
    public async Task<RegistryTestResult> TestAsync(Repository repo, int id, CancellationToken ct = default)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.DependencyBotRegistries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.RepositoryId == repo.Id, ct) ?? throw new InvalidOperationException("That registry no longer exists.");
        var endpoint = RegistrySet.Endpoint(row, protector);
        if (endpoint.BrokenReason != null) return new RegistryTestResult(false, endpoint.BrokenReason);
        try
        {
            var baseUrl = endpoint.Url.EndsWith('/') ? endpoint.Url : endpoint.Url + "/";
            switch (row.Ecosystem)
            {
                case Ecosystems.NuGet:
                    if (endpoint.Url.Split('?')[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        await client.ResolveNuGetBaseAsync(endpoint, new RegistrySet(), ct);
                        return new RegistryTestResult(true, "Connected: the service index lists a package base address" + (endpoint.Auth != null ? " and accepted the credential." : "."));
                    }
                    await http.GetStringAsync(baseUrl + "bytebench-connection-test/index.json", "application/json", ct, endpoint.Auth);
                    break;
                case Ecosystems.Npm: await http.GetStringAsync(baseUrl + "-/ping", "application/json", ct, endpoint.Auth); break;
                case Ecosystems.Pip:
                    await http.GetStringAsync(RegistryClient.IsSimpleIndex(endpoint.Url) ? baseUrl : baseUrl + "pip/json",
                        RegistryClient.IsSimpleIndex(endpoint.Url) ? "application/vnd.pypi.simple.v1+json" : "application/json", ct, endpoint.Auth);
                    break;
                default: await http.GetStringAsync(baseUrl + "golang.org/x/text/@v/list", "text/plain", ct, endpoint.Auth); break;
            }
            return new RegistryTestResult(true, "Connected" + (endpoint.Auth != null ? ": the registry accepted the credential." : "."));
        }
        catch (RegistryException ex) when (ex.NotFound)
        {
            return new RegistryTestResult(true, "The registry answered" + (endpoint.Auth != null ? " and did not reject the credential" : "") + " (it has no probe package or ping endpoint, so nothing more could be checked).");
        }
        catch (RegistryException ex) { return new RegistryTestResult(false, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new RegistryTestResult(false, "The test failed unexpectedly."); }
    }

    // ------------------------------------------------------------------ detection

    /// <summary>Registry addresses named by <c>.npmrc</c>, <c>nuget.config</c>, <c>pip.conf</c>, <c>pyproject.toml</c> and <c>requirements*.txt</c> of the repository, not yet configured.</summary>
    public async Task<List<DetectedRegistry>> DetectAsync(Repository repo)
    {
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await using var db = await dbFactory.CreateDbContextAsync();
        var branch = (await db.DependencyBotSettings.AsNoTracking().Where(s => s.RepositoryId == repo.Id).Select(s => s.TargetBranch).FirstOrDefaultAsync()) ?? repo.DefaultBranch;
        var configured = await db.DependencyBotRegistries.AsNoTracking().Where(r => r.RepositoryId == repo.Id).Select(r => new { r.Ecosystem, r.Scope, r.Url }).ToListAsync();
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var files = await Task.Run(() => RegistryDetector.ReadConfigFiles(path, branch));
        var publicHosts = new[] { "registry.npmjs.org", "api.nuget.org", "pypi.org", "proxy.golang.org" };
        return RegistryDetector.Detect(files)
            .Where(d => ValidateUrl(d.Url) == null
                && !(Uri.TryCreate(d.Url, UriKind.Absolute, out var u) && publicHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase))
                && !configured.Any(c => c.Ecosystem == d.Ecosystem && (d.Ecosystem == Ecosystems.NuGet ? c.Url.Equals(d.Url, StringComparison.OrdinalIgnoreCase) : c.Scope == d.Scope)))
            .ToList();
    }

    /// <summary>Accepts one detected address: it is added without any credential (the maintainer enters that by hand).</summary>
    public Task<RegistryView> AcceptDetectedAsync(Repository repo, DetectedRegistry detected, int? actorId = null) =>
        SaveAsync(repo, new RegistryInput { Ecosystem = detected.Ecosystem, Scope = detected.Scope, Url = detected.Url, AuthKind = DependencyRegistryAuth.None }, actorId);

    private static string Trunc(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
