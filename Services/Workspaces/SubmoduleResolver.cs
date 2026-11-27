using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;
using LgRepository = LibGit2Sharp.Repository;

namespace ByteBench.Services.Workspaces;

public sealed record SubmoduleLink(string Name, string Path, string Url);

/// <summary>What a workspace's git token may additionally read: submodule repositories on this ByteBench server that the owner can view.</summary>
public sealed record SubmoduleResolution(IReadOnlyList<int> RepositoryIds, IReadOnlyList<string> Notes);

/// <summary>Reads <c>.gitmodules</c> (git config syntax, only the keys the workspace needs).</summary>
public static partial class GitmodulesParser
{
    [GeneratedRegex("^\\[submodule\\s+\"(?<name>[^\"]{1,300})\"\\]$")] private static partial Regex Section();
    [GeneratedRegex("^(?<key>[A-Za-z][A-Za-z0-9-]*)\\s*=\\s*(?<value>.*)$")] private static partial Regex KeyValue();

    public const int MaxSubmodules = 100;

    public static List<SubmoduleLink> Parse(string? text)
    {
        var result = new List<SubmoduleLink>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 * 1024) return result;
        string? name = null, path = null, url = null;
        void Flush()
        {
            if (name != null && path != null && url != null && result.Count < MaxSubmodules) result.Add(new SubmoduleLink(name, path, url));
            path = null; url = null;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (Section().Match(line) is { Success: true } s) { Flush(); name = s.Groups["name"].Value; continue; }
            if (name == null || KeyValue().Match(line) is not { Success: true } kv) continue;
            var value = kv.Groups["value"].Value.Trim().Trim('"');
            switch (kv.Groups["key"].Value.ToLowerInvariant())
            {
                case "path": path = value; break;
                case "url": url = value; break;
            }
        }
        Flush();
        return result;
    }
}

/// <summary>
/// Works out which repositories on this server a workspace's submodules live in (so the workspace's git token, which is limited to one
/// repository, may also read those, and only those, never write). Relative URLs (<c>../lib.git</c>) and absolute ones that point at this
/// server's public address or at the address containers use are mapped; anything else is a foreign host and gets no credential at all.
/// The owner must be able to view the submodule's project right now (the git endpoint checks again on every request).
/// </summary>
public sealed partial class SubmoduleResolver(IDbContextFactory<AppDbContext> dbFactory, IServiceScopeFactory scopes, RepoStorage storage, Api.PublicBaseUrl publicBase)
{
    public const int MaxDepth = 4, MaxRepositories = 25;

    [GeneratedRegex("^/git/(?<key>[A-Za-z][A-Za-z0-9]{1,9})/(?<slug>[a-z0-9][a-z0-9._-]{0,99})\\.git/?$")]
    private static partial Regex GitPath();

    /// <summary>The git path (<c>/git/KEY/slug.git</c>) a submodule URL points at on this server, or null for foreign hosts and anything else.</summary>
    public static (string Key, string Slug)? MapUrl(string url, string superprojectKey, string superprojectSlug, Uri gitBase, string? publicBaseUrl)
    {
        url = url.Trim();
        if (url.Length == 0 || url.Length > 500) return null;
        Uri? abs;
        if (url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith("../", StringComparison.Ordinal))
        {
            // Relative to the superproject's remote URL, exactly as git resolves it.
            // git treats the repository's URL as a directory here: "../lib.git" beside "…/KEY/super.git" is "…/KEY/lib.git".
            var baseUri = new Uri(gitBase, $"git/{superprojectKey}/{superprojectSlug}.git/");
            return Uri.TryCreate(baseUri, url, out abs) ? FromPath(abs, gitBase) : null;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out abs) || abs.Scheme is not ("http" or "https")) return null;
        var pathBase = "";
        if (!string.IsNullOrEmpty(publicBaseUrl) && Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var pub)
            && string.Equals(pub.Authority, abs.Authority, StringComparison.OrdinalIgnoreCase)) pathBase = pub.AbsolutePath.TrimEnd('/');
        else if (!string.Equals(gitBase.Authority, abs.Authority, StringComparison.OrdinalIgnoreCase)) return null;
        var path = abs.AbsolutePath;
        if (pathBase.Length > 0 && path.StartsWith(pathBase + "/", StringComparison.Ordinal)) path = path[pathBase.Length..];
        return GitPath().Match(path) is { Success: true } m ? (m.Groups["key"].Value.ToUpperInvariant(), m.Groups["slug"].Value) : null;
    }

    private static (string Key, string Slug)? FromPath(Uri abs, Uri gitBase) =>
        string.Equals(abs.Authority, gitBase.Authority, StringComparison.OrdinalIgnoreCase) && GitPath().Match(abs.AbsolutePath) is { Success: true } m
            ? (m.Groups["key"].Value.ToUpperInvariant(), m.Groups["slug"].Value) : null;

    public async Task<SubmoduleResolution> ResolveAsync(string key, string slug, string sha, int ownerId, Uri gitBase, CancellationToken ct)
    {
        var ids = new List<int>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{key}/{slug}" };
        await using var scope = scopes.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<ProjectAccess>();
        access.ActAs(ownerId);
        await WalkAsync(key, slug, sha, 0);
        return new SubmoduleResolution(ids, notes);

        async Task WalkAsync(string k, string s, string revision, int depth)
        {
            if (depth >= MaxDepth || ids.Count >= MaxRepositories) return;
            string text;
            try { text = GitOps.GetBlob(storage.PathFor(k, s), revision, ".gitmodules")?.Text ?? ""; }
            catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException) { return; }
            foreach (var link in GitmodulesParser.Parse(text))
            {
                ct.ThrowIfCancellationRequested();
                var target = MapUrl(link.Url, k, s, gitBase, publicBase.Value);
                if (target == null) continue; // another host: cloned anonymously, never with this server's credential
                var (tk, ts) = target.Value;
                var found = await GitRepoLookup.FindAsync(dbFactory, tk, ts, ct);
                if (found == null || found.IsWiki) { notes.Add($"Submodule '{link.Path}' points at {tk}/{ts}, which doesn't exist on this server."); continue; }
                if (await access.GetRoleAsync(found.ProjectId, ownerId) == null) { notes.Add($"Submodule '{link.Path}' is {found.Key}/{found.Slug}: you don't have access to it, so it can't be fetched."); continue; }
                if (!ids.Contains(found.Id)) ids.Add(found.Id);
                var lookup = $"{found.Key}/{found.Slug}";
                if (!seen.Add(lookup + "@" + revision + ":" + link.Path)) continue;
                var pinned = PinnedSha(storage.PathFor(k, s), revision, link.Path);
                if (pinned != null) await WalkAsync(found.Key, found.Slug, pinned, depth + 1);
            }
        }
    }

    /// <summary>The commit a superproject pins a submodule path to (the gitlink in its tree), or null.</summary>
    internal static string? PinnedSha(string repoPath, string rev, string path)
    {
        try
        {
            using var r = new LgRepository(repoPath);
            var commit = LibGit2Sharp.RepositoryExtensions.Lookup<LibGit2Sharp.Commit>(r, rev) ?? r.Branches[rev]?.Tip;
            var entry = commit?[path.Trim('/')];
            return entry is { TargetType: LibGit2Sharp.TreeEntryTargetType.GitLink } ? entry.Target.Id.Sha : null;
        }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or ArgumentException) { return null; }
    }
}
