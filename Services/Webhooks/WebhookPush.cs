using System.IO.Compression;
using System.Text;
using ByteBench.Api;
using ByteBench.Data;
using ByteBench.Services.Git;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Webhooks;

/// <summary>One ref update requested by a push: <c>old new ref</c> from the receive-pack command list.</summary>
public sealed record PushedRef(string Before, string After, string Ref)
{
    public const string ZeroSha = "0000000000000000000000000000000000000000";
    public bool Created => Before == ZeroSha;
    public bool Deleted => After == ZeroSha;
}

/// <summary>
/// Per-request (scoped) record of the ref updates in a <c>git-receive-pack</c> request. <see cref="WebhookPushCaptureMiddleware"/>
/// tees the start of the request body; the commands are parsed on first use (after git has consumed the body).
/// Any <see cref="IGitPushListener"/> may inject this to learn which refs a push moved.
/// </summary>
public sealed class PushCapture
{
    internal MemoryStream? Buffer;
    internal bool Gzipped;
    private IReadOnlyList<PushedRef>? refs;

    public bool IsAvailable => Buffer != null;

    public IReadOnlyList<PushedRef> Refs => refs ??= Buffer == null ? [] : Parse(Buffer.ToArray(), Gzipped);

    /// <summary>
    /// Records ref updates ByteBench made itself (a web editor commit), so the push listeners that read this capture see
    /// them exactly as they would see a git push's.
    /// </summary>
    public void SetRefs(IReadOnlyList<PushedRef> updates)
    {
        Buffer = new MemoryStream();
        Gzipped = false;
        refs = updates;
    }

    /// <summary>Parses pkt-lines "XXXX&lt;old&gt; &lt;new&gt; &lt;ref&gt;[\0caps]\n" up to the first flush packet.</summary>
    public static IReadOnlyList<PushedRef> Parse(byte[] data, bool gzipped)
    {
        if (gzipped)
        {
            using var output = new MemoryStream();
            try
            {
                using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                gz.CopyTo(output);
            }
            catch (InvalidDataException) { /* truncated prefix: keep what was inflated */ }
            catch (EndOfStreamException) { }
            data = output.ToArray();
        }

        var result = new List<PushedRef>();
        var pos = 0;
        while (pos + 4 <= data.Length)
        {
            if (!int.TryParse(Encoding.ASCII.GetString(data, pos, 4), System.Globalization.NumberStyles.HexNumber, null, out var len)) break;
            if (len == 0) break; // flush: end of the command list
            if (len < 4 || pos + len > data.Length) break;
            var line = Encoding.UTF8.GetString(data, pos + 4, len - 4);
            pos += len;
            var nul = line.IndexOf('\0');
            if (nul >= 0) line = line[..nul];
            var parts = line.TrimEnd('\n').Split(' ');
            if (parts.Length == 3 && parts[0].Length == 40 && parts[1].Length == 40 && parts[2].StartsWith("refs/", StringComparison.Ordinal))
                result.Add(new PushedRef(parts[0], parts[1], parts[2]));
        }
        return result;
    }
}

/// <summary>
/// Wraps the body of <c>POST /git/{KEY}/{repo}.git/git-receive-pack</c> so the first 64 KB (which hold the ref-update
/// commands) are copied into <see cref="PushCapture"/> while git reads it. Nothing else is touched. Also teaches
/// <see cref="PublicBaseUrl"/> the site's URL from the first request.
/// </summary>
public sealed class WebhookPushCaptureMiddleware(RequestDelegate next, PublicBaseUrl baseUrl)
{
    private const int CaptureBytes = 64 * 1024;

    public Task InvokeAsync(HttpContext ctx)
    {
        baseUrl.Learn(ctx.Request);
        if (HttpMethods.IsPost(ctx.Request.Method)
            && ctx.Request.Path.StartsWithSegments("/git", StringComparison.Ordinal)
            && ctx.Request.Path.Value!.EndsWith(".git/git-receive-pack", StringComparison.Ordinal))
        {
            var capture = ctx.RequestServices.GetRequiredService<PushCapture>();
            capture.Buffer = new MemoryStream();
            capture.Gzipped = ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);
            ctx.Request.Body = new TeeReadStream(ctx.Request.Body, capture.Buffer, CaptureBytes);
        }
        return next(ctx);
    }

    /// <summary>Read-only pass-through stream that copies the first <c>limit</c> bytes it reads into <c>copy</c>.</summary>
    private sealed class TeeReadStream(Stream inner, MemoryStream copy, int limit) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Copy(buffer.AsSpan(offset, n));
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Copy(buffer.Span[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private void Copy(ReadOnlySpan<byte> data)
        {
            var room = limit - (int)copy.Length;
            if (room > 0) copy.Write(data[..Math.Min(room, data.Length)]);
        }
    }
}

/// <summary>Raises a <c>push</c> webhook event for each ref a git push over HTTP actually moved.</summary>
public sealed class WebhookPushListener(IDbContextFactory<AppDbContext> dbFactory, PushCapture capture,
    WebhookPushEvents events, ILogger<WebhookPushListener> log) : IGitPushListener
{
    public async Task OnPushAsync(int repositoryId, int userId, CancellationToken ct)
    {
        if (!capture.IsAvailable)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
            if (repo != null && await WebhookNotifier.AnyListenerAsync(db, repo.ProjectId, WebhookEventNames.Push))
                log.LogWarning("Push to {Key}/{Slug}: ref updates were not captured; no push webhook sent", repo.Project.Key, repo.Slug);
            return;
        }
        await events.SendAsync(repositoryId, capture.Refs, userId, ct);
    }
}

/// <summary>
/// Builds and enqueues <c>push</c> events for ref updates, whether they came from a git push (<see cref="WebhookPushListener"/>)
/// or from ByteBench itself (merging a pull request moves its target branch and may delete its source branch). Every
/// ref is checked against the repository first, so only refs that really are at <c>after</c> are reported.
/// </summary>
public sealed class WebhookPushEvents(IDbContextFactory<AppDbContext> dbFactory, RepoStorage storage,
    WebhookDispatcher dispatcher, PublicBaseUrl baseUrl, ILogger<WebhookPushEvents> log)
{
    public const int MaxCommits = 20;

    /// <summary>A branch ref update made inside ByteBench (<paramref name="before"/>/<paramref name="after"/> null for a create/delete).</summary>
    public static PushedRef Branch(string branch, string? before, string? after) =>
        new(before ?? PushedRef.ZeroSha, after ?? PushedRef.ZeroSha, "refs/heads/" + branch);

    /// <summary>Enqueues one <c>push</c> event per ref that moved. Never throws (failures are logged): call it after the change is saved.</summary>
    public async Task SendAsync(int repositoryId, IReadOnlyList<PushedRef> refs, int userId, CancellationToken ct = default)
    {
        try { await SendCoreAsync(repositoryId, refs, userId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Couldn't raise push webhooks for repository {RepositoryId}", repositoryId);
        }
    }

    private async Task SendCoreAsync(int repositoryId, IReadOnlyList<PushedRef> refs, int userId, CancellationToken ct)
    {
        if (refs.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repositories.AsNoTracking().Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo == null || !await WebhookNotifier.AnyListenerAsync(db, repo.ProjectId, WebhookEventNames.Push)) return;

        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        var repoDto = Dto.Repository(repo, baseUrl.Value);
        foreach (var pushed in refs)
        {
            var info = await Task.Run(() => Describe(path, pushed), ct);
            if (info == null) continue; // rejected by git (e.g. non-fast-forward): the ref didn't move
            dispatcher.Enqueue(repo.ProjectId, WebhookEventNames.Push, new
            {
                @ref = pushed.Ref,
                before = pushed.Before,
                after = pushed.After,
                created = pushed.Created,
                deleted = pushed.Deleted,
                repository = repoDto,
                commits = info.Value.Commits,
                totalCommits = info.Value.Total,
                headCommit = info.Value.Commits.FirstOrDefault(),
                compareUrl = pushed.Created || pushed.Deleted ? null : $"{repoDto.WebUrl}/commits?ref={Uri.EscapeDataString(pushed.After)}",
            }, userId);
        }
    }

    /// <summary>Commits the push introduced (newest first, at most <see cref="MaxCommits"/>), or null if the ref isn't at <c>after</c>.</summary>
    private static (List<CommitDto> Commits, int Total)? Describe(string path, PushedRef pushed)
    {
        using var r = new LibGit2Sharp.Repository(path);
        var current = r.Refs[pushed.Ref]?.ResolveToDirectReference()?.TargetIdentifier;
        if (pushed.Deleted) return current == null ? ([], 0) : null;
        if (current != pushed.After) return null;
        if (r.Lookup(pushed.After) is not Commit after) return ([], 0); // e.g. an annotated tag object: no commit list
        var exclude = new List<Commit>();
        if (!pushed.Created && r.Lookup(pushed.Before) is Commit before) exclude.Add(before);
        if (pushed.Created) // a new ref: commits not on any other branch
            exclude.AddRange(r.Branches.Where(b => !b.IsRemote && b.CanonicalName != pushed.Ref && b.Tip != null).Select(b => b.Tip));
        var filter = new CommitFilter { IncludeReachableFrom = after, ExcludeReachableFrom = exclude, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        var all = r.Commits.QueryBy(filter).Take(1000).ToList();
        var commits = all.Take(MaxCommits).Select(c => new CommitDto(c.Sha, c.Message, c.Author.Name, c.Author.Email,
            c.Author.When.ToUniversalTime(), c.Parents.Select(p => p.Sha).ToList())).ToList();
        return (commits, all.Count);
    }
}
