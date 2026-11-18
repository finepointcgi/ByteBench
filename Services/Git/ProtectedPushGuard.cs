using System.IO.Compression;
using System.Text;

namespace GitBench.Services.Git;

/// <summary>
/// Enforces branch protection on <c>git push</c> over HTTP. Before the request reaches <c>git http-backend</c> it
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
/// </summary>
internal static class ProtectedPushGuard
{
    public sealed record Result(bool Rejected, Stream? Body, IReadOnlyDictionary<string, string>? Environment)
    {
        public static readonly Result PassThrough = new(false, null, null);
    }

    private sealed record Command(string OldSha, string NewSha, string Ref);

    private const int MaxCommandBytes = 4 * 1024 * 1024;
    private const string HookFolder = ".hooks";

    public static async Task<Result> InspectAsync(HttpContext ctx, ILogger logger, int repositoryId, int projectId, int userId, RepoStorage storage)
    {
        var protection = ctx.RequestServices.GetRequiredService<BranchProtectionService>();
        var rules = await protection.RulesForAsync(repositoryId);
        if (rules.Count == 0) return Result.PassThrough;
        var isAdmin = await protection.IsBypassAdminAsync(projectId, userId);

        var encoding = ctx.Request.Headers.ContentEncoding.ToString();
        var source = encoding is "gzip" or "x-gzip" ? new GZipStream(ctx.Request.Body, CompressionMode.Decompress) : ctx.Request.Body;

        var (prefix, commands, caps, signed) = await ReadCommandsAsync(source, ctx.RequestAborted);
        var body = new PrefixedStream(prefix, source);
        if (commands.Count == 0 && !signed) return new Result(false, body, null);

        var refusals = new Dictionary<string, string>();
        var fastForwardOnly = new List<string>();
        foreach (var c in commands)
        {
            if (!c.Ref.StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
            var branch = c.Ref["refs/heads/".Length..];
            var p = BranchProtectionService.ApplicableTo(rules, branch, isAdmin);
            if (!p.IsProtected) continue;
            var kind = IsZero(c.NewSha) ? RefChangeKind.Delete : IsZero(c.OldSha) ? RefChangeKind.Create : RefChangeKind.Update;
            if (BranchProtectionService.Refusal(p, branch, kind, isFastForward: null) is { } why) refusals[c.Ref] = why;
            else if (kind == RefChangeKind.Update && p.BlockForcePushes) fastForwardOnly.Add(c.Ref);
        }
        if (signed) refusals["(signed push)"] = "signed pushes can't be checked against branch protection";

        if (refusals.Count > 0)
        {
            logger.LogInformation("Push to repository {Id} by user {User} refused by branch protection: {Refusals}",
                repositoryId, userId, string.Join("; ", refusals.Select(r => $"{r.Key}: {r.Value}")));
            await DrainAsync(body, ctx.RequestAborted);
            await WriteRefusalAsync(ctx, commands, caps, refusals);
            return new Result(true, null, null);
        }

        if (fastForwardOnly.Count == 0) return new Result(false, body, null);
        var hooks = EnsureHook(storage);
        return new Result(false, body, new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = hooks,
            ["GITBENCH_FF_ONLY_REFS"] = string.Join(' ', fastForwardOnly),
        });
    }

    private static bool IsZero(string sha) => sha.All(ch => ch == '0');

    /// <summary>Reads pkt-lines up to the first flush. Returns the raw bytes read (to replay) and the parsed commands.</summary>
    private static async Task<(byte[] Prefix, List<Command> Commands, HashSet<string> Caps, bool Signed)> ReadCommandsAsync(Stream source, CancellationToken ct)
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
        return (raw.ToArray(), commands, caps, signed);
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

    private static async Task DrainAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[81920];
        try { while (await body.ReadAsync(buffer, ct) > 0) { } }
        catch (IOException) { /* client gave up: nothing to drain */ }
        catch (InvalidDataException) { /* corrupt gzip: nothing more to read */ }
    }

    /// <summary>Answers the push like receive-pack would: "unpack ok", then "ng ref reason" for every ref.</summary>
    private static async Task WriteRefusalAsync(HttpContext ctx, List<Command> commands, HashSet<string> caps, Dictionary<string, string> refusals)
    {
        var sideband = caps.Contains("side-band-64k") || caps.Contains("side-band");
        var reportStatus = caps.Contains("report-status") || caps.Contains("report-status-v2");

        var report = new MemoryStream();
        if (reportStatus)
        {
            Pkt(report, "unpack ok\n");
            foreach (var c in commands)
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

        if (output.Length == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Push refused by branch protection: " + string.Join("; ", refusals.Values) + "\n");
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/x-git-receive-pack-result";
        await ctx.Response.Body.WriteAsync(output.ToArray());
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
        # Declines non-fast-forward updates of the refs listed in GITBENCH_FF_ONLY_REFS (protected branches).
        status=0
        while read -r old new ref; do
          case " $GITBENCH_FF_ONLY_REFS " in *" $ref "*) ;; *) continue ;; esac
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
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
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
