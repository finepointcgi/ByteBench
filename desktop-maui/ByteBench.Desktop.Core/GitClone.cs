using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteBench.Desktop.Core;

public sealed record RepoRef(string Key, string Slug);

public sealed record CloneResult(bool Ok, string Message, string? Directory = null);

/// <summary>"Clone with ByteBench": runs <c>git clone -- &lt;server&gt;/git/KEY/slug.git &lt;folder&gt;/slug</c> with an argument list (no shell).</summary>
public static partial class GitClone
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")] private static partial Regex KeyRegex();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")] private static partial Regex SlugRegex();
    [GeneratedRegex(@"^/p/([^/]+)/repos/([^/]+)(?:/|$)")] private static partial Regex PageRegex();
    [GeneratedRegex(@"^/git/([^/]+)/([^/]+?)\.git(?:/|$)")] private static partial Regex GitRegex();

    /// <summary>Repository for a page path such as /p/WEB/repos/api/blob/main/x.cs or a clone path /git/WEB/api.git.</summary>
    public static RepoRef? RepoFromPath(string? pathname)
    {
        if (pathname is null) return null;
        var m = PageRegex().Match(pathname);
        if (!m.Success) m = GitRegex().Match(pathname);
        if (!m.Success) return null;
        string key, slug;
        try { key = Uri.UnescapeDataString(m.Groups[1].Value); slug = Uri.UnescapeDataString(m.Groups[2].Value); }
        catch (UriFormatException) { return null; }
        return KeyRegex().IsMatch(key) && SlugRegex().IsMatch(slug) && !slug.EndsWith(".git", StringComparison.Ordinal) ? new RepoRef(key, slug) : null;
    }

    public static string CloneUrl(string origin, RepoRef repo) =>
        $"{origin.TrimEnd('/')}/git/{Uri.EscapeDataString(repo.Key)}/{Uri.EscapeDataString(repo.Slug)}.git";

    /// <summary>Arguments for git. The URL never contains credentials; "--" stops a hostile value being read as an option.</summary>
    public static IReadOnlyList<string> CloneArgs(string url, string targetDir) => ["clone", "--", url, targetDir];

    /// <summary>Extra environment so a token authenticates without appearing in the process list.</summary>
    public static IReadOnlyDictionary<string, string> CloneEnv(string? username, string? token)
    {
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            env["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}";
        }
        return env;
    }

    /// <summary>The folder the repository is cloned into: parent/slug. It must not exist yet (or be empty).</summary>
    public static (bool Ok, string Dir, string? Error) TargetDirectory(string parent, RepoRef repo)
    {
        var full = Path.GetFullPath(parent);
        var dir = Path.Combine(full, repo.Slug);
        if (!string.Equals(Path.GetDirectoryName(dir), full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return (false, "", "Invalid folder name.");
        if (Directory.Exists(dir))
        {
            try { if (Directory.EnumerateFileSystemEntries(dir).Any()) return (false, "", $"The folder {dir} already exists and is not empty."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (false, "", $"Cannot use {dir}."); }
        }
        else if (File.Exists(dir)) return (false, "", $"{dir} is a file.");
        return (true, dir, null);
    }

    /// <summary>The process to start: no shell, every argument separate.</summary>
    public static ProcessStartInfo BuildStartInfo(string gitExecutable, string url, string targetDir, string? username = null, string? token = null)
    {
        var psi = new ProcessStartInfo(gitExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in CloneArgs(url, targetDir)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in CloneEnv(username, token)) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>Finds an executable by walking PATH (no shell, no `which`).</summary>
    public static string? FindOnPath(string name, string? envPath, bool isWindows)
    {
        var exts = isWindows ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var dir in (envPath ?? "").Split(isWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Runs git clone. Never throws for a failed clone; the result carries git's message.</summary>
    public static async Task<CloneResult> RunAsync(string origin, RepoRef repo, string parentFolder, string? username, string? token, string? gitExecutable = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var git = gitExecutable ?? FindOnPath("git", Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        if (git is null) return new(false, "git was not found on PATH. Install git and try again.");
        var target = TargetDirectory(parentFolder, repo);
        if (!target.Ok) return new(false, target.Error!);

        using var psi = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, psi.Token);
        using var proc = new Process { StartInfo = BuildStartInfo(git, CloneUrl(origin, repo), target.Dir, username, token) };
        try
        {
            proc.Start();
            proc.StandardInput.Close();
            var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var err = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                var text = err.Trim();
                return new(false, text.Length == 0 ? $"git exited with code {proc.ExitCode}." : text.Length > 1000 ? text[..1000] : text);
            }
            return new(true, "Cloned.", target.Dir);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new(false, ct.IsCancellationRequested ? "Cancelled." : "The clone took too long and was stopped.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new(false, $"Could not start git: {e.Message}");
        }
    }
}
