using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GitBench.Data;
using GitBench.Services.Git;
using GitBench.Services.Hosting;
using GitBench.Services.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services.Ssh;

/// <summary>
/// Runs one exec command on the SSH server: <c>git-upload-pack '&lt;path&gt;'</c> (clone, fetch) or
/// <c>git-receive-pack '&lt;path&gt;'</c> (push), with the path <c>KEY/slug.git</c> (leading <c>/</c> and <c>.git</c> optional;
/// old slugs follow renames). The same rules as git over HTTP apply:
/// <list type="bullet">
/// <item>user keys act as their user through <see cref="ProjectAccess"/> (ViewProject to read, PushCode to push; a project
/// the user can't see answers "Repository not found"); deploy keys reach only their own repository, read-only unless
/// they have write access;</item>
/// <item>archived repositories refuse pushes; per-user (or per-key) rate limit <c>RateLimiting:Ssh</c>;</item>
/// <item>branch protection and release tags: the ref commands are read from the client before receive-pack sees them and
/// checked with <see cref="ProtectedPushGuard.Policy"/>, exactly as for HTTP. A refused push gets receive-pack's own
/// report (<c>! [remote rejected] main -&gt; main (reason)</c>) and receive-pack is told the client sent nothing, so no
/// object or ref is written. Force pushes to protected branches are caught by the same pre-receive hook as HTTP;</item>
/// <item>after a successful push the <see cref="IGitPushListener"/>s run, with <see cref="PushCapture"/> filled from the
/// ref commands (webhooks, CI, smart commits, stale approvals, AI review).</item>
/// </list>
/// Errors before git starts are sent as an <c>ERR</c> pkt-line, which git prints as <c>fatal: remote error: …</c>.
/// </summary>
internal sealed class SshGitCommand(IServiceScopeFactory scopes, AppRateLimits rateLimits, RepoStorage storage, string? gitPath,
    ILogger log)
{
    private const string UploadPack = "git-upload-pack";
    private const string ReceivePack = "git-receive-pack";

    /// <summary>
    /// Runs the command, then calls <paramref name="finish"/> with the exit status and, when git didn't run, a message to send
    /// before the channel closes. Push listeners run after that, so the client isn't kept waiting for webhooks or CI.
    /// </summary>
    public async Task RunAsync(SshIdentity id, string remote, string command, string? gitProtocol, Stream channel,
        Func<int, byte[]?, Task> finish, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var (exit, output, pushedBy) = await RunCoreAsync(scope.ServiceProvider, id, remote, command, gitProtocol, channel, ct);
        try { await finish(exit, output); }
        catch (Exception ex) when (pushedBy != null && ex is IOException or ObjectDisposedException or OperationCanceledException
                                       or Microsoft.DevTunnels.Ssh.SshChannelException or Microsoft.DevTunnels.Ssh.SshConnectionException)
        {
            log.LogDebug(ex, "Client left before the SSH channel was closed"); // the push is in: still tell the listeners
        }
        if (pushedBy is { } p) await GitRepoLookup.NotifyPushListenersAsync(scope.ServiceProvider, log, p.RepositoryId, p.UserId);
    }

    private async Task<(int Exit, byte[]? Output, (int RepositoryId, int UserId)? Pushed)> RunCoreAsync(IServiceProvider services, SshIdentity id,
        string remote, string command, string? gitProtocol, Stream channel, CancellationToken ct)
    {
        var parsed = SshGitCommandParser.Parse(command);
        if (parsed == null)
        {
            log.LogInformation("SSH: refused command {Command} from {Name} at {Remote}", Truncate(command), id.Name, remote);
            return (1, Encoding.UTF8.GetBytes(
                "GitBench only runs git over SSH (clone, fetch and push), e.g. git clone ssh://git@<host>:<port>/KEY/repository.git\n"), null);
        }

        if (rateLimits.TryAcquireSsh(id.IsDeployKey ? "d:" + id.KeyId : "u:" + id.UserId) is { } wait)
        {
            log.LogWarning("SSH rate limit hit by {Name} from {Remote}", id.Name, remote);
            return Error($"Too many requests. Try again in {wait} seconds.");
        }
        if (gitPath == null) return Error("Git is not available on this server.");
        if (!GitRepoLookup.IsValid(parsed.ProjectKey, parsed.Slug)) return NotFound();

        var found = await GitRepoLookup.FindAsync(services.GetRequiredService<IDbContextFactory<AppDbContext>>(), parsed.ProjectKey, parsed.Slug, ct);
        if (found == null) return NotFound();

        var writing = parsed.Service == ReceivePack;
        int? actingUser; // whose permissions apply and who push listeners act as
        if (id.IsDeployKey)
        {
            if (found.Id != id.DeployRepositoryId) return NotFound(); // a deploy key only exists for its own repository
            if (writing && !id.DeployCanWrite)
                return Error("This deploy key is read-only. A Maintainer can allow write access in the repository's deploy key settings.");
            actingUser = id.DeployCreatorId;
            if (writing && !await IsActiveUserAsync(services, actingUser, ct))
                return Error("The user who added this deploy key is no longer active, so it can't push. Add the key again.");
        }
        else
        {
            actingUser = id.UserId;
            var role = await services.GetRequiredService<ProjectAccess>().GetRoleAsync(found.ProjectId, id.UserId);
            if (role is null) return NotFound(); // don't reveal private repositories
            if (writing && !ProjectAccess.RoleHas(role.Value, Permission.PushCode))
            {
                log.LogInformation("SSH push to {Key}/{Slug} denied for {User} (role {Role})", found.Key, found.Slug, id.Name, role);
                return Error($"You don't have permission to push to this repository (requires Developer; you are {role}).");
            }
        }
        if (writing && found.IsArchived)
        {
            log.LogInformation("SSH push to archived {Key}/{Slug} rejected for {User}", found.Key, found.Slug, id.Name);
            return Error(GitRepoLookup.ArchivedMessage);
        }
        if (GitRepoLookup.DiskPath(storage, found, log) is not { } diskPath) return NotFound();

        if (actingUser is { } uid) services.GetRequiredService<ProjectAccess>().ActAs(uid); // push listeners run as this user

        log.LogInformation("SSH {Service} {Key}/{Slug} by {Name} from {Remote}", parsed.Service, found.Key, found.Slug, id.Name, remote);
        return writing
            ? await ReceivePackAsync(services, id, actingUser, found, diskPath, gitProtocol, channel, ct)
            : (await RunGitAsync("upload-pack", diskPath, gitProtocol, null, channel, CopyAllAsync, ct), null, null);
    }

    private async Task<(int, byte[]?, (int, int)?)> ReceivePackAsync(IServiceProvider services, SshIdentity id, int? actingUser,
        GitRepoLookup.RepoInfo found, string diskPath, string? gitProtocol, Stream channel, CancellationToken ct)
    {
        // Deploy keys push like a Developer: never an admin bypass.
        var policy = await ProtectedPushGuard.LoadPolicyAsync(services, found.Id, found.ProjectId, id.IsDeployKey ? null : id.UserId);
        string? refsFile = null;
        IReadOnlyDictionary<string, string>? env = null;
        if (policy.Rules.Any(r => r.BlockForcePushes))
        {
            refsFile = Path.Combine(Path.GetTempPath(), "gitbench-ff-" + Guid.NewGuid().ToString("N"));
            env = ProtectedPushGuard.HookEnvironmentWithRefsFile(storage, refsFile);
        }

        ProtectedPushGuard.ParsedCommands? commands = null;
        byte[]? refusal = null;

        // receive-pack advertises its refs, then the client sends "old new ref" commands, a flush and the pack. The
        // commands are read here, checked, and only then replayed to git, so a refused push never reaches it.
        async Task PumpStdinAsync(Stream input, Stream stdin, CancellationToken token)
        {
            commands = await ProtectedPushGuard.ReadCommandsAsync(input, token);
            var decision = policy.Check(commands);
            if (decision.Refused)
            {
                ProtectedPushGuard.LogRefusal(log, found.Id, id.Name, decision);
                refusal = ProtectedPushGuard.RefusalReport(commands, decision.Refusals);
                await stdin.WriteAsync("0000"u8.ToArray(), token); // "nothing to update": receive-pack exits without writing
                return;
            }
            if (refsFile != null) await File.WriteAllTextAsync(refsFile, string.Join(' ', decision.FastForwardOnly), token);
            await stdin.WriteAsync(commands.Prefix, token);
            await CopyAllAsync(input, stdin, token);
        }

        try
        {
            var exit = await RunGitAsync("receive-pack", diskPath, gitProtocol, env, channel, PumpStdinAsync, ct);
            if (refusal != null)
            {
                // The client is still sending its pack, then waits for the report: answer, and read until it hangs up.
                var drain = DrainAsync(channel, ct);
                if (refusal.Length > 0) await channel.WriteAsync(refusal, ct);
                await drain;
                return (0, null, null);
            }
            if (exit == 0 && commands is { Commands.Count: > 0 })
            {
                if (actingUser is { } uid)
                {
                    // The listeners (run by RunAsync once the client has its answer) learn the ref updates from PushCapture.
                    var capture = services.GetRequiredService<PushCapture>();
                    capture.Buffer = new MemoryStream(commands.Prefix);
                    capture.Gzipped = false;
                    log.LogInformation("Push to {Key}/{Slug} by {Name} over SSH", found.Key, found.Slug, id.Name);
                    return (exit, null, (found.Id, uid));
                }
                log.LogWarning("Push to {Key}/{Slug} with deploy key {KeyId} has no acting user; push listeners skipped", found.Key, found.Slug, id.KeyId);
            }
            return (exit, null, null);
        }
        finally
        {
            if (refsFile != null) try { File.Delete(refsFile); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Runs <c>git &lt;service&gt; &lt;dir&gt;</c> with the channel wired to stdin (through <paramref name="pumpStdin"/>) and stdout;
    /// stderr is logged. Returns git's exit code.
    /// </summary>
    private async Task<int> RunGitAsync(string service, string diskPath, string? gitProtocol, IReadOnlyDictionary<string, string>? extraEnv,
        Stream channel, Func<Stream, Stream, CancellationToken, Task> pumpStdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(gitPath!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = storage.Root,
        };
        psi.ArgumentList.Add(service);
        psi.ArgumentList.Add(diskPath);
        // Don't let the server's own git environment (GIT_DIR, GIT_CONFIG_*, ...) leak in.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        if (gitProtocol != null) psi.Environment["GIT_PROTOCOL"] = gitProtocol;
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        using var kill = ct.Register(() => TryKill(process));

        var stderr = DrainStderrAsync(process.StandardError, service);
        using var stdinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stdin = Task.Run(async () =>
        {
            var target = process.StandardInput.BaseStream;
            try { await pumpStdin(channel, target, stdinCts.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                log.LogDebug(ex, "git {Service} stdin closed", service);
            }
            finally
            {
                try { await target.DisposeAsync(); } catch (IOException) { }
            }
        }, CancellationToken.None);

        await process.StandardOutput.BaseStream.CopyToAsync(channel, 81920, ct);
        await channel.FlushAsync(ct);
        await process.WaitForExitAsync(ct);
        await stderr;
        // git is done; a client that keeps its side open must not keep the pump waiting.
        await stdinCts.CancelAsync();
        try { await stdin.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (TimeoutException) { }
        if (process.ExitCode != 0) log.LogInformation("git {Service} exited with {Code} for {Path}", service, process.ExitCode, diskPath);
        return process.ExitCode;
    }

    private static async Task CopyAllAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int n;
        while ((n = await from.ReadAsync(buffer, ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
            await to.FlushAsync(ct);
        }
    }

    /// <summary>Reads (and discards) what the client still sends, until it closes its side or 10 minutes pass.</summary>
    private static async Task DrainAsync(Stream channel, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var buffer = new byte[81920];
        try { while (await channel.ReadAsync(buffer, timeout.Token) > 0) { } }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private async Task DrainStderrAsync(StreamReader stderr, string service)
    {
        string? line;
        while ((line = await stderr.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("fatal:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
                log.LogWarning("git {Service} (ssh): {Line}", service, line);
            else
                log.LogDebug("git {Service} (ssh): {Line}", service, line);
        }
    }

    private void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { log.LogDebug(ex, "Failed to kill git"); }
    }

    private static async Task<bool> IsActiveUserAsync(IServiceProvider services, int? userId, CancellationToken ct)
    {
        if (userId == null) return false;
        var now = DateTime.UtcNow;
        await using var db = await services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.DeactivatedAt, u.LockoutEnd }).FirstOrDefaultAsync(ct);
        return user != null && user.DeactivatedAt == null && !(user.LockoutEnd > now); // LockoutEnd can't be compared in SQLite
    }

    private static (int, byte[]?, (int, int)?) NotFound() => Error("Repository not found.");

    /// <summary>An <c>ERR</c> pkt-line: git stops and prints <c>fatal: remote error: &lt;message&gt;</c>.</summary>
    public static (int, byte[]?, (int, int)?) Error(string message)
    {
        var line = "ERR GitBench: " + message + "\n";
        return (1, Encoding.UTF8.GetBytes((Encoding.UTF8.GetByteCount(line) + 4).ToString("x4") + line), null);
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

/// <summary>
/// Parses the exec command of an SSH session: <c>git-upload-pack '/KEY/slug.git'</c> or <c>git-receive-pack '…'</c> (also
/// <c>git upload-pack</c>, an unquoted path, no leading <c>/</c> or no <c>.git</c>). Anything else is refused.
/// </summary>
public static partial class SshGitCommandParser
{
    public sealed record Command(string Service, string ProjectKey, string Slug);

    // git-upload-pack '/WEB/web-app.git' (git quotes the path with single quotes; "git upload-pack" is the same command)
    [GeneratedRegex(@"^git[ -](upload-pack|receive-pack) +(?:'(?<q>(?:[^']|'\\'')*)'|(?<p>[^\s'""]+))$")]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"^/?(?<key>[^/]+)/(?<slug>[^/]+?)(?:\.git)?/?$")]
    private static partial Regex PathPattern();

    /// <summary>Parses an exec command, or returns null for anything that isn't a git upload-pack/receive-pack of a repository path.</summary>
    public static Command? Parse(string command)
    {
        var m = CommandPattern().Match(command.Trim());
        if (!m.Success) return null;
        var path = m.Groups["q"].Success ? m.Groups["q"].Value.Replace(@"'\''", "'") : m.Groups["p"].Value;
        var pm = PathPattern().Match(path);
        if (!pm.Success) return null;
        return new Command("git-" + m.Groups[1].Value, pm.Groups["key"].Value, pm.Groups["slug"].Value);
    }
}
