using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Code;

public sealed record CodeSearchOptions(string Query, bool IgnoreCase = true, bool Regex = false);

/// <summary>One line in a result snippet. <see cref="Hits"/> are (start, length) ranges of the match within <see cref="Text"/>.</summary>
public sealed record SearchLine(int Number, string Text, bool IsMatch, IReadOnlyList<(int Start, int Length)> Hits);

/// <summary>Matches in one file: contiguous runs of lines (matches plus a little context), separated by gaps.</summary>
public sealed record SearchFileResult(string Path, int MatchCount, IReadOnlyList<IReadOnlyList<SearchLine>> Snippets);

public sealed record RepoSearchResult(
    Repository Repository, string Ref, IReadOnlyList<string> FileNames, IReadOnlyList<SearchFileResult> Files,
    int MatchCount, bool Truncated, string? Error)
{
    public bool IsEmpty => FileNames.Count == 0 && Files.Count == 0;
}

/// <summary>
/// Code search over bare repositories with <c>git grep</c> (content) and <c>git ls-tree</c> (file names), run as child
/// processes with an argument list (no shell), a timeout and result caps. Reads require <see cref="Permission.ViewProject"/>.
/// </summary>
public class CodeSearchService(
    IDbContextFactory<AppDbContext> dbFactory, RepositoryService repos, ProjectAccess access, IConfiguration config,
    ILogger<CodeSearchService> logger)
{
    public const int MaxMatchesPerRepo = 200;
    public const int MaxMatchesTotal = 400;
    public const int MaxFileNameMatches = 50;
    public const int ContextLines = 1;
    private const int MaxLineChars = 500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private string? gitPath;
    private string? GitPath => gitPath ??= GitHttpBackend.LocateGit(config);

    /// <summary>Searches one repository at <paramref name="rev"/> (default branch when empty).</summary>
    public async Task<RepoSearchResult> SearchRepositoryAsync(Repository repo, string? rev, CodeSearchOptions options,
        int maxMatches = MaxMatchesPerRepo, CancellationToken ct = default)
    {
        var @ref = string.IsNullOrEmpty(rev) ? repo.DefaultBranch : rev;
        RepoSearchResult Fail(string error) => new(repo, @ref, [], [], 0, false, error);

        if (!await access.CanAsync(repo.ProjectId, Permission.ViewProject)) return Fail("Repository not found.");
        if (string.IsNullOrWhiteSpace(options.Query)) return Fail("Enter something to search for.");
        if (options.Query.Length > 200) return Fail("The search text is too long (max 200 characters).");
        Regex? matcher;
        try { matcher = BuildMatcher(options); }
        catch (ArgumentException ex) { return Fail("Invalid regular expression: " + ex.Message); }
        if (GitPath is null) return Fail("Search is unavailable: git was not found on the server.");

        var path = repos.PathOf(repo);
        var sha = await Task.Run(() => GitOps.Exists(path) ? GitOps.ResolveSha(path, @ref) : null, ct);
        if (sha is null) return Fail($"'{@ref}' doesn't exist in this repository.");

        try
        {
            var names = await FileNameMatchesAsync(path, sha, matcher, ct);
            var (hits, truncated) = await GrepAsync(path, sha, options, maxMatches, ct);
            var files = await Task.Run(() => BuildFiles(path, sha, hits, matcher), ct);
            return new RepoSearchResult(repo, @ref, names, files, hits.Count, truncated, null);
        }
        catch (TimeoutException)
        {
            return Fail("The search took too long and was stopped. Try a more specific query.");
        }
        catch (GitCommandException ex)
        {
            logger.LogWarning("git failed for search in {Path}: {Error}", path, ex.Message);
            return Fail(options.Regex ? "Search failed. Check the regular expression." : "Search failed.");
        }
    }

    /// <summary>Searches the default branch of every repository in the project.</summary>
    public async Task<List<RepoSearchResult>> SearchProjectAsync(int projectId, CodeSearchOptions options, CancellationToken ct = default)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.Repositories.Include(r => r.Project).Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Name).ToListAsync(ct);
        var results = new List<RepoSearchResult>();
        var budget = MaxMatchesTotal;
        foreach (var repo in list)
        {
            if (budget <= 0)
            {
                results.Add(new RepoSearchResult(repo, repo.DefaultBranch, [], [], 0, true, null));
                continue;
            }
            var r = await SearchRepositoryAsync(repo, null, options, Math.Min(budget, MaxMatchesPerRepo), ct);
            budget -= r.MatchCount;
            results.Add(r);
            if (r.Error != null && r.Error.StartsWith("Invalid regular expression")) break; // same for every repo
        }
        return results;
    }

    private static Regex? BuildMatcher(CodeSearchOptions o)
    {
        var flags = RegexOptions.CultureInvariant | (o.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(o.Regex ? o.Query : Regex.Escape(o.Query), flags, TimeSpan.FromMilliseconds(200));
    }

    private async Task<List<string>> FileNameMatchesAsync(string path, string sha, Regex? matcher, CancellationToken ct)
    {
        var result = new List<string>();
        await RunGitAsync(path, ["ls-tree", "-r", "-z", "--name-only", "--full-tree", sha], record =>
        {
            if (Safe(matcher, record)) result.Add(record);
            return result.Count < MaxFileNameMatches;
        }, ct);
        return result;
    }

    private sealed record GrepHit(string Path, int Line);

    /// <summary>Set once git reports it was built without PCRE; regex searches then fall back to POSIX ERE.</summary>
    private static bool pcreUnavailable;

    private async Task<(List<GrepHit> Hits, bool Truncated)> GrepAsync(string path, string sha, CodeSearchOptions o, int max, CancellationToken ct)
    {
        if (!o.Regex || pcreUnavailable) return await GrepAsync(path, sha, o, max, o.Regex ? "--extended-regexp" : "--fixed-strings", ct);
        try
        {
            // Perl-compatible regexes behave like the .NET regex used to mark matches (\w, \d, lazy quantifiers…).
            return await GrepAsync(path, sha, o, max, "--perl-regexp", ct);
        }
        catch (GitCommandException ex) when (ex.Message.Contains("PCRE", StringComparison.OrdinalIgnoreCase))
        {
            pcreUnavailable = true;
            return await GrepAsync(path, sha, o, max, "--extended-regexp", ct);
        }
    }

    private async Task<(List<GrepHit> Hits, bool Truncated)> GrepAsync(string path, string sha, CodeSearchOptions o, int max, string mode, CancellationToken ct)
    {
        // -z: NUL after the "<sha>:<path>" and after the line number, so paths containing ':' parse correctly.
        // Line text is not needed here; context and match ranges are taken from the blob afterwards.
        var args = new List<string> { "grep", "-n", "-I", "-z", "--no-color", mode };
        if (o.IgnoreCase) args.Add("--ignore-case");
        args.AddRange(["--max-count", "50", "-e", o.Query, sha, "--"]);

        var hits = new List<GrepHit>();
        var truncated = false;
        var prefix = sha + ":";
        await RunGitAsync(path, args, record =>
        {
            // record = "<sha>:<path>\0<line>\0<text>" (we split on '\n' only, so the NULs are inside the record)
            var parts = record.Split('\0', 3);
            if (parts.Length < 2 || !parts[0].StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(parts[1], out var line)) return true;
            if (hits.Count >= max) { truncated = true; return false; }
            hits.Add(new GrepHit(parts[0][prefix.Length..], line));
            return true;
        }, ct, separator: '\n', okExitCodes: [0, 1]);
        return (hits, truncated);
    }

    private static List<SearchFileResult> BuildFiles(string repoPath, string sha, List<GrepHit> hits, Regex? matcher)
    {
        var files = new List<SearchFileResult>();
        foreach (var group in hits.GroupBy(h => h.Path))
        {
            var lines = GitOps.GetTextLines(repoPath, sha, group.Key);
            var matchLines = group.Select(h => h.Line).Distinct().Order().ToList();
            var snippets = new List<IReadOnlyList<SearchLine>>();
            if (lines != null)
            {
                List<SearchLine>? current = null;
                var lastAdded = 0;
                foreach (var m in matchLines)
                {
                    var from = Math.Max(1, m - ContextLines);
                    var to = Math.Min(lines.Length, m + ContextLines);
                    if (current == null || from > lastAdded + 1)
                    {
                        current = [];
                        snippets.Add(current);
                    }
                    for (var n = Math.Max(from, lastAdded + 1); n <= to; n++)
                    {
                        var text = lines[n - 1];
                        if (text.Length > MaxLineChars) text = text[..MaxLineChars] + "…";
                        var isMatch = matchLines.BinarySearch(n) >= 0;
                        current.Add(new SearchLine(n, text, isMatch, isMatch ? Ranges(matcher, text) : []));
                        lastAdded = n;
                    }
                }
            }
            files.Add(new SearchFileResult(group.Key, matchLines.Count, snippets));
        }
        return files;
    }

    private static List<(int, int)> Ranges(Regex? matcher, string text)
    {
        var list = new List<(int, int)>();
        if (matcher == null) return list;
        try
        {
            foreach (Match m in matcher.Matches(text))
            {
                if (m.Length == 0) continue;
                list.Add((m.Index, m.Length));
                if (list.Count >= 20) break;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return list;
    }

    private static bool Safe(Regex? matcher, string text)
    {
        try { return matcher?.IsMatch(text) == true; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private sealed class GitCommandException(string message) : Exception(message);

    /// <summary>
    /// Runs git against a bare repo and feeds each <paramref name="separator"/>-terminated record to <paramref name="onRecord"/>
    /// until it returns false (then the process is killed). Throws <see cref="TimeoutException"/> after <see cref="Timeout"/>.
    /// </summary>
    private async Task RunGitAsync(string repoPath, IEnumerable<string> args, Func<string, bool> onRecord, CancellationToken ct,
        char separator = '\0', int[]? okExitCodes = null)
    {
        var psi = new ProcessStartInfo(GitPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.ArgumentList.Add("--git-dir");
        psi.ArgumentList.Add(repoPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(psi) ?? throw new GitCommandException("could not start git");
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var stopped = false;
        try
        {
            var reader = process.StandardOutput;
            var sb = new StringBuilder();
            var buffer = new char[8192];
            int read;
            while (!stopped && (read = await reader.ReadAsync(buffer, timeout.Token)) > 0)
            {
                for (var i = 0; i < read && !stopped; i++)
                {
                    if (buffer[i] != separator) { sb.Append(buffer[i]); continue; }
                    if (!onRecord(sb.ToString())) stopped = true;
                    sb.Clear();
                }
            }
            if (!stopped && sb.Length > 0) onRecord(sb.ToString());
            if (stopped) Kill(process);
            else await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException();
        }
        catch
        {
            Kill(process);
            throw;
        }

        if (!stopped && !(okExitCodes ?? [0]).Contains(process.ExitCode))
        {
            var err = "";
            try { err = await stderrTask; } catch (OperationCanceledException) { }
            throw new GitCommandException($"git exited with {process.ExitCode}: {err.Trim()}");
        }
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
