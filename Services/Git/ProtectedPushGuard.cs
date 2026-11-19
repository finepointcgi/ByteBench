using System.IO.Compression;
using System.Text;
using GitBench.Data;

namespace GitBench.Services.Git;

/// <summary>
/// Enforces branch protection on <c>git push</c> over HTTP (and, through the shared pieces below, over SSH). Before the request reaches <c>git http-backend</c> it
/// reads the receive-pack ref update commands (<c>old-sha new-sha refname</c> pkt-lines, up to the first flush) from
/// the request body and checks each branch against <see cref="BranchProtectionService"/>:
/// <list type="bullet">
/// <item>Deleting a branch with "block deletion", or creating/updating one with "block direct pushes", is refused
/// right here: the whole push is answered with a receive-pack report (<c>ng refs/heads/main &lt;reason&gt;</c>), plus a
/// sideband message, so git prints <c>! [remote rejected] main -> main (reason)</c> and nothing is written.</item>
/// <item>Force pushes can only be detected once the pack has arrived, so updates of branches with "block force
/// pushes" are handed to a small <c>pre-receive</c> hook (enabled for that request only, through
/// <c>core.hooksPath</c> in the CGI environment) that runs <c>git merge-base --is-ancestor old new</c> and declines
/// the push when the old tip isn't an ancestor of the new one.</item>
/// </list>
/// The bytes read are replayed to http-backend unchanged (gzip bodies are inflated first), so the push itself is not
/// affected. Repositories without rules skip all of this.
/// <para>
/// Protected tags (<see cref="ProtectedTags"/>) and pull mirrors (every ref is refused: the repository follows its
/// upstream) are decided by the same <see cref="Policy"/>.
/// The checks are transport-agnostic and shared with git over SSH (<see cref="Ssh.SshGitCommand"/>):
/// <see cref="ReadCommandsAsync"/> parses the command list, <see cref="Policy.Check"/> decides, <see cref="RefusalReport"/>
/// builds receive-pack's answer, and the hook environment enables the force-push check.
/// </para>
/// </summary>
internal static class ProtectedPushGuard
{
    public sealed record Result(bool Rejected, Stream? Body, IReadOnlyDictionary<string, string>? Environment)
    {
        public static readonly Result PassThrough = new(false, null, null);
    }

    /// <summary>One ref update: <c>old-sha new-sha refname</c>.</summary>
    public sealed record Command(string OldSha, string NewSha, string Ref);

    /// <summary>The receive-pack command list: the raw bytes read (to replay to git), the commands and the client's capabilities.</summary>
    public sealed record ParsedCommands(byte[] Prefix, List<Command> Commands, HashSet<string> Caps, bool Signed);

    /// <summary>
    /// The protection that applies to one pusher in one repository. <see cref="IsAdmin"/> (site admin or project Owner)
    /// skips rules that allow admin bypass; deploy keys never bypass.
    /// </summary>
    public sealed record Policy(IReadOnlyList<BranchProtectionRule> Rules, IReadOnlySet<string> ReleaseTags, bool IsAdmin)
    {
        /// <summary>Protected tag rules (<see cref="ProtectedTags"/>), checked with <see cref="Role"/>.</summary>
        public IReadOnlyList<ProtectedTagRule> TagRules { get; init; } = [];
        /// <summary>The pusher's role in the project (deploy keys: Developer); only matters for protected tags.</summary>
        public ProjectRole Role { get; init; } = ProjectRole.Developer;
        /// <summary>Set when the repository refuses every push (a pull mirror): the reason.</summary>
        public string? ReadOnlyReason { get; init; }

        public bool IsEmpty => Rules.Count == 0 && ReleaseTags.Count == 0 && TagRules.Count == 0 && ReadOnlyReason == null;

        /// <summary>Which refs are refused (ref → reason) and which protected branches may only fast-forward.</summary>
        public Decision Check(ParsedCommands parsed)
        {
            var refusals = new Dictionary<string, string>();
            var fastForwardOnly = new List<string>();
            foreach (var c in parsed.Commands)
            {
                if (ReadOnlyReason != null) { refusals[c.Ref] = ReadOnlyReason; continue; }
                if (c.Ref.StartsWith(GitOps.MergeQueueRefPrefix, StringComparison.Ordinal)) { refusals[c.Ref] = "refs/merge-queue/ is managed by the merge queue"; continue; }
                if (c.Ref.StartsWith("refs/tags/", StringComparison.Ordinal))
                {
                    var tag = c.Ref["refs/tags/".Length..];
                    var tagKind = IsZero(c.NewSha) ? RefChangeKind.Delete : IsZero(c.OldSha) ? RefChangeKind.Create : RefChangeKind.Update;
                    if (ProtectedTags.Refusal(TagRules, tag, tagKind, Role) is { } tagWhy) refusals[c.Ref] = tagWhy;
                }
                if (c.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) && IsZero(c.NewSha) && ReleaseTags.Contains(c.Ref["refs/tags/".Length..]))
                    refusals[c.Ref] = $"tag '{c.Ref["refs/tags/".Length..]}' is used by a release: delete the release first";
                if (!c.Ref.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
                var branch = c.Ref["refs/heads/".Length..];
                var p = BranchProtectionService.ApplicableTo(Rules, branch, IsAdmin);
                if (!p.IsProtected) continue;
                var kind = IsZero(c.NewSha) ? RefChangeKind.Delete : IsZero(c.OldSha) ? RefChangeKind.Create : RefChangeKind.Update;
                if (BranchProtectionService.Refusal(p, branch, kind, isFastForward: null) is { } why) refusals[c.Ref] = why;
                else if (kind == RefChangeKind.Update && p.BlockForcePushes) fastForwardOnly.Add(c.Ref);
            }
            if (parsed.Signed && !IsEmpty) refusals["(signed push)"] = "signed pushes can't be checked against branch protection";
            return new Decision(refusals, fastForwardOnly);
        }
    }

    public sealed record Decision(Dictionary<string, string> Refusals, List<string> FastForwardOnly)
    {
        public bool Refused => Refusals.Count > 0;
    }

    private const int MaxCommandBytes = 4 * 1024 * 1024;
    private const string HookFolder = ".hooks";

    /// <summary>
    /// Loads the branch protection rules and release tags of the repository. <paramref name="userId"/> is the pusher, or
    /// null for a deploy key (treated like a Developer: no admin bypass).
    /// </summary>
    public static async Task<Policy> LoadPolicyAsync(IServiceProvider services, int repositoryId, int projectId, int? userId)
    {
        var protection = services.GetRequiredService<BranchProtectionService>();
        var rules = await protection.RulesForAsync(repositoryId);
        var dbFactory = services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<GitBench.Data.AppDbContext>>();
        HashSet<string> releaseTags; // tags used by releases can't be deleted by a push
        await using (var db = await dbFactory.CreateDbContextAsync())
            releaseTags = (await Releases.ReleaseService.ReleaseTagsAsync(db, repositoryId)).ToHashSet(StringComparer.Ordinal);
        var isAdmin = userId != null && rules.Count > 0 && await protection.IsBypassAdminAsync(projectId, userId);

        // Protected tags, and pull mirrors (read-only: only the mirror itself, which doesn't push, updates their refs).
        List<ProtectedTagRule> tagRules;
        string? readOnly = null;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            tagRules = await ProtectedTagService.RulesForAsync(db, repositoryId);
            if (await Mirroring.MirrorService.IsPullMirrorAsync(db, repositoryId)) readOnly = Mirroring.MirrorService.PullMirrorReadOnlyMessage;
        }
        var role = ProjectRole.Developer; // deploy keys push like a Developer
        if (userId != null && tagRules.Count > 0)
            role = await services.GetRequiredService<ProjectAccess>().GetRoleAsync(projectId, userId) ?? ProjectRole.Viewer;
        return new Policy(rules, releaseTags, isAdmin) { TagRules = tagRules, Role = role, ReadOnlyReason = readOnly };
    }

    /// <summary>HTTP: reads the commands from the request body, refuses the push or passes it on (with the force-push hook).</summary>
    public static async Task<Result> InspectAsync(HttpContext ctx, ILogger logger, int repositoryId, int projectId, int userId, RepoStorage storage)
    {
        var policy = await LoadPolicyAsync(ctx.RequestServices, repositoryId, projectId, userId);
        if (policy.IsEmpty) return Result.PassThrough;

        var encoding = ctx.Request.Headers.ContentEncoding.ToString();
        var source = encoding is "gzip" or "x-gzip" ? new GZipStream(ctx.Request.Body, CompressionMode.Decompress) : ctx.Request.Body;

        var parsed = await ReadCommandsAsync(source, ctx.RequestAborted);
        var body = new PrefixedStream(parsed.Prefix, source);
        if (parsed.Commands.Count == 0 && !parsed.Signed) return new Result(false, body, null);

        var decision = policy.Check(parsed);
        if (decision.Refused)
        {
            LogRefusal(logger, repositoryId, userId.ToString(System.Globalization.CultureInfo.InvariantCulture), decision);
            await DrainAsync(body, ctx.RequestAborted);
            await WriteRefusalAsync(ctx, parsed, decision.Refusals);
            return new Result(true, null, null);
        }

        return new Result(false, body, HookEnvironment(storage, decision.FastForwardOnly));
    }

    public static void LogRefusal(ILogger logger, int repositoryId, string pusher, Decision decision) =>
        logger.LogInformation("Push to repository {Id} by {User} refused by branch protection: {Refusals}",
            repositoryId, pusher, string.Join("; ", decision.Refusals.Select(r => $"{r.Key}: {r.Value}")));

    /// <summary>
    /// Environment for <c>receive-pack</c> / <c>http-backend</c> that enables the force-push <c>pre-receive</c> hook for
    /// <paramref name="fastForwardOnly"/>; null when there is nothing to check.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? HookEnvironment(RepoStorage storage, IReadOnlyCollection<string> fastForwardOnly)
    {
        if (fastForwardOnly.Count == 0) return null;
        return HookEnvironment(storage, "GITBENCH_FF_ONLY_REFS", string.Join(' ', fastForwardOnly));
    }

    /// <summary>
    /// SSH: enables the force-push hook for a receive-pack started before the ref commands arrive. The hook reads the
    /// fast-forward-only refs from <paramref name="refsFile"/>, which the caller writes once it has read the commands.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HookEnvironmentWithRefsFile(RepoStorage storage, string refsFile) =>
        HookEnvironment(storage, "GITBENCH_FF_ONLY_FILE", refsFile);

    private static Dictionary<string, string> HookEnvironment(RepoStorage storage, string variable, string value)
    {
        var hooks = EnsureHook(storage);
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = hooks,
            [variable] = value,
        };
    }

    private static bool IsZero(string sha) => sha.All(ch => ch == '0');

    /// <summary>Reads pkt-lines up to the first flush. Returns the raw bytes read (to replay) and the parsed commands.</summary>
    public static async Task<ParsedCommands> ReadCommandsAsync(Stream source, CancellationToken ct)
    {
        var raw = new MemoryStream();
        var commands = new List<Command>();
        var caps = new HashSet<string>(StringComparer.Ordinal);
        var signed = false;
        var header = new byte[4];
        while (raw.Length < MaxCommandBytes)
        {
            if (!await ReadExactlyOrEofAsync(source, header, ct)) break;
            raw.Write(header);
            if (!int.TryParse(Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var len)) break;
            if (len == 0) break;          // flush: end of the command list
            if (len < 4 || len > 65520) break; // not a v0 command pkt-line: leave it to git
            var payload = new byte[len - 4];
            if (!await ReadExactlyOrEofAsync(source, payload, ct)) { raw.Write(payload); break; }
            raw.Write(payload);

            var line = Encoding.UTF8.GetString(payload);
            var nul = line.IndexOf('\0');
            if (nul >= 0)
            {
                foreach (var cap in line[(nul + 1)..].TrimEnd('\n').Split(' ', StringSplitOptions.RemoveEmptyEntries)) caps.Add(cap);
                line = line[..nul];
            }
            line = line.TrimEnd('\n');
            if (line.StartsWith("shallow ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("push-cert", StringComparison.Ordinal)) { signed = true; break; }
            var parts = line.Split(' ');
            if (parts.Length == 3) commands.Add(new Command(parts[0], parts[1], parts[2]));
        }
        return new ParsedCommands(raw.ToArray(), commands, caps, signed);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static async Task DrainAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[81920];
        try { while (await body.ReadAsync(buffer, ct) > 0) { } }
        catch (IOException) { /* client gave up: nothing to drain */ }
        catch (InvalidDataException) { /* corrupt gzip: nothing more to read */ }
    }

    /// <summary>HTTP: the refusal report as the receive-pack response, or a plain 403 if the client asked for neither report nor sideband.</summary>
    private static async Task WriteRefusalAsync(HttpContext ctx, ParsedCommands parsed, Dictionary<string, string> refusals)
    {
        var output = RefusalReport(parsed, refusals);
        if (output.Length == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Push refused by branch protection: " + string.Join("; ", refusals.Values) + "\n");
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/x-git-receive-pack-result";
        await ctx.Response.Body.WriteAsync(output);
    }

    /// <summary>
    /// Answers the push like receive-pack would: "unpack ok", then "ng ref reason" for every ref, with the reasons also on
    /// sideband 2 (git prints them as <c>remote: …</c>). Empty when the client asked for neither report-status nor sideband.
    /// </summary>
    public static byte[] RefusalReport(ParsedCommands parsed, Dictionary<string, string> refusals)
    {
        var caps = parsed.Caps;
        var sideband = caps.Contains("side-band-64k") || caps.Contains("side-band");
        var reportStatus = caps.Contains("report-status") || caps.Contains("report-status-v2");

        var report = new MemoryStream();
        if (reportStatus)
        {
            Pkt(report, "unpack ok\n");
            foreach (var c in parsed.Commands)
                Pkt(report, refusals.TryGetValue(c.Ref, out var why)
                    ? $"ng {c.Ref} {why}\n"
                    : $"ng {c.Ref} not updated: another ref in this push was refused by branch protection\n");
            report.Write("0000"u8);
        }

        var output = new MemoryStream();
        if (sideband)
        {
            var max = caps.Contains("side-band-64k") ? 65515 : 995;
            foreach (var why in refusals.Values.Distinct())
                Band(output, 2, Encoding.UTF8.GetBytes($"GitBench: push refused: {why}\n"), max);
            if (report.Length > 0) Band(output, 1, report.ToArray(), max);
            output.Write("0000"u8);
        }
        else
        {
            report.WriteTo(output);
        }
        return output.ToArray();
    }

    private static void Pkt(Stream s, string text) => Pkt(s, Encoding.UTF8.GetBytes(text));

    private static void Pkt(Stream s, ReadOnlySpan<byte> data)
    {
        s.Write(Encoding.ASCII.GetBytes((data.Length + 4).ToString("x4")));
        s.Write(data);
    }

    private static void Band(Stream s, byte band, byte[] data, int max)
    {
        for (var i = 0; i < data.Length; i += max)
        {
            var chunk = data.AsSpan(i, Math.Min(max, data.Length - i));
            var framed = new byte[chunk.Length + 1];
            framed[0] = band;
            chunk.CopyTo(framed.AsSpan(1));
            Pkt(s, framed);
        }
    }

    // ---------- pre-receive hook for force-push detection ----------

    private const string HookScript = """
        #!/bin/sh
        # Installed by GitBench (Services/Git/ProtectedPushGuard.cs) and enabled per push through core.hooksPath.
        # Declines non-fast-forward updates of the refs listed in GITBENCH_FF_ONLY_REFS (protected branches), plus those
        # in the file GITBENCH_FF_ONLY_FILE (SSH pushes: the refs are only known after receive-pack has started).
        status=0
        refs="$GITBENCH_FF_ONLY_REFS"
        if [ -n "$GITBENCH_FF_ONLY_FILE" ] && [ -f "$GITBENCH_FF_ONLY_FILE" ]; then refs="$refs $(cat "$GITBENCH_FF_ONLY_FILE")"; fi
        while read -r old new ref; do
          case " $refs " in *" $ref "*) ;; *) continue ;; esac
          case "$old" in *[!0]*) ;; *) continue ;; esac
          case "$new" in *[!0]*) ;; *) continue ;; esac
          if ! git merge-base --is-ancestor "$old" "$new" 2>/dev/null; then
            echo "GitBench: push refused: branch '${ref#refs/heads/}' is protected: force pushes are not allowed" >&2
            status=1
          fi
        done
        exit $status

        """;

    private static readonly Lock HookLock = new();

    /// <summary>Writes the hook under the repository root (once, or when it changed) and returns its folder.</summary>
    private static string EnsureHook(RepoStorage storage)
    {
        var dir = Path.Combine(storage.Root, HookFolder);
        var file = Path.Combine(dir, "pre-receive");
        var script = HookScript.Replace("\r\n", "\n");
        lock (HookLock)
        {
            if (File.Exists(file) && File.ReadAllText(file) == script) return dir;
            Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, script);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, file, overwrite: true);
        }
        return dir;
    }

    /// <summary>Replays <c>prefix</c>, then the rest of <c>inner</c>.</summary>
    public sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int pos;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (pos < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - pos);
                prefix.AsMemory(pos, n).CopyTo(buffer);
                pos += n;
                return n;
            }
            return await inner.ReadAsync(buffer, ct);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
