using GitBench.Data;
using GitBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace GitBench.Services;

/// <summary>How to merge a pull request.</summary>
/// <param name="AllowFastForward">Merge strategy only: move the target to the source tip instead of writing a merge commit, when possible.</param>
/// <param name="CommitMessage">Squash only: the squash commit's message (null: <see cref="PullRequestService.DefaultSquashMessage"/>).
/// Co-authored-by trailers for the other commit authors are appended unless already present.</param>
public sealed record MergeRequest(MergeStrategy Strategy = MergeStrategy.Merge, bool DeleteSourceBranch = false, bool AllowFastForward = false,
    string? CommitMessage = null);

/// <summary>The thread view of a PR's inline comments.</summary>
/// <param name="Outdated">Ids of thread roots whose line(s) changed after the comment (by a later push).</param>
/// <param name="Unresolved">Number of inline threads not resolved yet (outdated ones included).</param>
public sealed record ThreadSummary(IReadOnlySet<int> Outdated, int Unresolved);

/// <summary>Per-repository merge strategy settings (see <see cref="Repository.AllowedMergeStrategies"/>).</summary>
public static class MergeStrategySettings
{
    public static MergeStrategies Flag(MergeStrategy s) => (MergeStrategies)(1 << (int)s);
    public static MergeStrategies Allowed(Repository r) => r.AllowedMergeStrategies is { } a && a != MergeStrategies.None ? a & MergeStrategies.All : MergeStrategies.All;
    public static IEnumerable<MergeStrategy> AllowedList(Repository r) => Enum.GetValues<MergeStrategy>().Where(s => Allowed(r).HasFlag(Flag(s)));

    /// <summary>The repository's default strategy, or the first allowed one if the default isn't allowed.</summary>
    public static MergeStrategy Default(Repository r) =>
        r.DefaultMergeStrategy is { } d && Allowed(r).HasFlag(Flag(d)) ? d : AllowedList(r).First();

    public static string Label(MergeStrategy s) => s switch
    {
        MergeStrategy.Squash => "Squash",
        MergeStrategy.Rebase => "Rebase",
        _ => "Merge commit",
    };

    public static string Describe(MergeStrategy s) => s switch
    {
        MergeStrategy.Squash => "Combine every commit into one new commit on the target branch.",
        MergeStrategy.Rebase => "Replay each commit onto the target branch and fast-forward it (linear history, no merge commit).",
        _ => "Keep every commit and add a merge commit (or fast-forward, when chosen and possible).",
    };

    /// <summary>Parses "merge", "squash" or "rebase" (any case); null for anything else.</summary>
    public static MergeStrategy? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "merge" or "merge_commit" or "merge-commit" => MergeStrategy.Merge,
        "squash" => MergeStrategy.Squash,
        "rebase" => MergeStrategy.Rebase,
        _ => null,
    };
}

/// <summary>Drafts, comment threads, suggested changes, code owners and merge strategies.</summary>
public partial class PullRequestService
{
    // ---------- Drafts ----------

    /// <summary>
    /// Converts the PR to a draft, or marks it ready for review. Marking it ready notifies its assigned reviewers (who
    /// weren't notified while it was a draft). The author (with CreatePullRequest) or a Maintainer+ may do this.
    /// </summary>
    public async Task SetDraftAsync(int prId, int actorId, bool draft)
    {
        await RequireAuthorOrMergerAsync(prId, actorId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.FindAsync(prId) ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Only open pull requests can change their draft status.");
        if (pr.IsDraft == draft) return;
        pr.IsDraft = draft;
        Log(db, pr, actorId, draft ? "converted this pull request to a draft" : "marked this pull request as ready for review");
        await db.SaveChangesAsync();
        if (!draft)
        {
            var reviewerIds = await db.PullRequestReviewers.Where(r => r.PullRequestId == prId).Select(r => r.UserId).ToListAsync();
            await notifications.OnReviewersAddedAsync(prId, actorId, reviewerIds);
        }
        else await notifications.OnPullRequestCommentChangedAsync(prId); // live refresh only
        await webhooks.PullRequestUpdatedAsync(prId, actorId, draft ? "converted_to_draft" : "ready_for_review");
    }

    // ---------- Threads ----------

    /// <summary>The PR's author, the thread's author and Maintainers+ may resolve and unresolve a thread.</summary>
    public static bool CanResolve(PullRequest pr, PullRequestComment root, int userId, ProjectPermissions perms) =>
        perms.Can(Permission.Comment) && (pr.AuthorId == userId || root.AuthorId == userId || perms.Can(Permission.MergePullRequest));

    /// <summary>Resolves (or reopens) the thread that <paramref name="commentId"/> belongs to.</summary>
    public async Task SetThreadResolvedAsync(int commentId, int actorId, bool resolved)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.PullRequestComments.FirstOrDefaultAsync(x => x.Id == commentId) ?? throw new InvalidOperationException("Comment not found.");
        var root = c.ParentId is { } pid ? await db.PullRequestComments.FirstAsync(x => x.Id == pid) : c;
        if (!root.IsInline) throw new InvalidOperationException("Only inline comment threads can be resolved.");
        var pr = await db.PullRequests.Include(p => p.Repository).FirstAsync(p => p.Id == root.PullRequestId);
        var perms = await access.GetPermissionsAsync(pr.Repository.ProjectId, actorId);
        if (!CanResolve(pr, root, actorId, perms))
            throw new ForbiddenException("Only the pull request's author, the comment's author or a Maintainer can resolve this thread.");
        if (root.IsResolved == resolved) return;
        root.IsResolved = resolved;
        root.ResolvedById = resolved ? actorId : null;
        root.ResolvedAt = resolved ? DateTime.UtcNow : null;
        Log(db, pr, actorId, $"{(resolved ? "resolved" : "unresolved")} a thread on `{root.FilePath}:{root.Line ?? root.OldLine}`");
        await db.SaveChangesAsync();
        await notifications.OnPullRequestCommentChangedAsync(pr.Id);
    }

    /// <summary>Which threads are outdated at <paramref name="headSha"/> (the source tip) and how many are unresolved.</summary>
    public async Task<ThreadSummary> ThreadsAsync(PullRequest pr, string? headSha)
    {
        var roots = pr.Comments.Where(c => c.IsThreadRoot).ToList();
        var unresolved = roots.Count(c => !c.IsResolved);
        if (headSha == null) return new ThreadSummary(new HashSet<int>(), unresolved);
        var check = roots.Where(c => c.CommitSha != null && c.CommitSha != headSha).ToList();
        if (check.Count == 0) return new ThreadSummary(new HashSet<int>(), unresolved);
        var outdated = await repos.Git(pr.Repository, path => GitOps.Exists(path) ? OutdatedThreads(path, headSha, check) : []);
        return new ThreadSummary(outdated, unresolved);
    }

    /// <summary>
    /// A new-side thread is outdated when its line(s) at <paramref name="head"/> no longer read what was commented on; a
    /// thread on a deleted line, or one without recorded line text, when its file changed since the comment.
    /// </summary>
    internal static HashSet<int> OutdatedThreads(string repoPath, string head, IEnumerable<PullRequestComment> roots)
    {
        var result = new HashSet<int>();
        var blobs = new Dictionary<(string Rev, string Path), BlobInfo?>();
        BlobInfo? Blob(string rev, string file) =>
            blobs.TryGetValue((rev, file), out var b) ? b : blobs[(rev, file)] = GitOps.GetBlob(repoPath, rev, file);
        foreach (var c in roots)
        {
            var now = Blob(head, c.FilePath!);
            if (c.Line is { } end && c.LineText != null)
            {
                var start = c.StartLine ?? end;
                var lines = now?.Lines ?? [];
                if (end > lines.Length || string.Join("\n", lines[(start - 1)..end]) != c.LineText) result.Add(c.Id);
            }
            else if (c.CommitSha != null && Blob(c.CommitSha, c.FilePath!)?.Sha != now?.Sha) result.Add(c.Id);
        }
        return result;
    }

    /// <summary>Fills in a new comment's thread and anchors: replies copy their thread's position; new inline threads record the head and line text.</summary>
    private async Task AnchorAsync(AppDbContext db, PullRequestComment c, int? parentId, int? startLine)
    {
        if (parentId is { } pid)
        {
            var parent = await db.PullRequestComments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid && x.PullRequestId == c.PullRequestId)
                         ?? throw new InvalidOperationException("The comment you're replying to doesn't exist.");
            var root = parent.ParentId is { } rid ? await db.PullRequestComments.AsNoTracking().FirstAsync(x => x.Id == rid) : parent;
            if (!root.IsInline) throw new InvalidOperationException("Only inline comments have threads.");
            c.ParentId = root.Id;
            (c.FilePath, c.Line, c.OldLine, c.StartLine, c.CommitSha, c.LineText) = (root.FilePath, root.Line, root.OldLine, root.StartLine, root.CommitSha, root.LineText);
            return;
        }
        if (c.FilePath == null) return;
        if (c.Line == null && c.OldLine == null) throw new InvalidOperationException("An inline comment needs a line.");
        if (startLine is { } s && (c.Line is not { } e || s < 1 || s > e)) throw new InvalidOperationException("startLine must be at most line.");
        c.StartLine = startLine == c.Line ? null : startLine;

        var pr = await db.PullRequests.AsNoTracking().Include(p => p.Repository).ThenInclude(r => r.Project).FirstAsync(p => p.Id == c.PullRequestId);
        var rev = pr.State == PullRequestState.Open ? pr.SourceBranch : pr.SourceCommitSha;
        if (rev == null) return;
        (c.CommitSha, c.LineText) = await repos.Git(pr.Repository, path =>
        {
            if (!GitOps.Exists(path) || GitOps.ResolveSha(path, rev) is not { } head) return ((string?)null, (string?)null);
            return (head, c.Line is { } line ? GitOps.ReadLines(path, head, c.FilePath, c.StartLine ?? line, line) : null);
        });
    }

    // ---------- Suggested changes ----------

    /// <summary>
    /// Whether <paramref name="userId"/> may apply suggestions to the PR: it's open, the repository isn't archived,
    /// they can push code and branch protection lets them update the source branch.
    /// </summary>
    public async Task<bool> CanApplySuggestionsAsync(PullRequest pr, int userId)
    {
        if (pr.State != PullRequestState.Open || pr.Repository.IsArchived) return false;
        if (!await access.CanAsync(pr.Repository.ProjectId, Permission.PushCode, userId)) return false;
        try
        {
            await protection.EnsureAllowedAsync(pr.Repository, pr.SourceBranch, RefChangeKind.Update, userId);
            return true;
        }
        catch (BranchProtectedException) { return false; }
    }

    /// <summary>
    /// Applies the suggestions of the given comments as ONE commit on the PR's source branch (author: the actor;
    /// message "Apply suggestion from @user"; co-authored by the suggestions' authors), then resolves their threads.
    /// Refused, with nothing written, if any suggested line changed since the comment, two suggestions overlap,
    /// or the actor can't push to the source branch.
    /// </summary>
    public async Task<string> ApplySuggestionsAsync(int prId, User actor, IReadOnlyCollection<int> commentIds)
    {
        await access.RequireForPullRequestAsync(prId, Permission.PushCode, actor.Id);
        if (commentIds.Count == 0) throw new InvalidOperationException("Choose at least one suggestion to apply.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var pr = await db.PullRequests.Include(p => p.Repository).ThenInclude(r => r.Project)
            .FirstOrDefaultAsync(p => p.Id == prId) ?? throw new InvalidOperationException("Pull request not found.");
        if (pr.State != PullRequestState.Open) throw new InvalidOperationException("Suggestions can only be applied to open pull requests.");
        RepositorySettingsService.EnsureNotArchived(pr.Repository);
        await protection.EnsureAllowedAsync(pr.Repository, pr.SourceBranch, RefChangeKind.Update, actor.Id);

        var ids = commentIds.Distinct().ToList();
        var comments = await db.PullRequestComments.Include(c => c.Author)
            .Where(c => c.PullRequestId == prId && ids.Contains(c.Id)).ToListAsync();
        if (comments.Count != ids.Count) throw new InvalidOperationException("Some of those comments don't belong to this pull request.");
        var edits = new List<LineEdit>();
        foreach (var c in comments.OrderBy(c => c.Id))
        {
            var suggestion = Suggestions.Parse(c.Body);
            if (suggestion == null || c.FilePath == null || c.Line is not { } end)
                throw new InvalidOperationException($"Comment #{c.Id} doesn't suggest a change to new lines of a file.");
            if (c.AppliedCommitSha != null) throw new InvalidOperationException($"The suggestion in comment #{c.Id} was already applied.");
            if (c.LineText == null) throw new InvalidOperationException($"The suggestion in comment #{c.Id} can't be applied: its original lines weren't recorded.");
            edits.Add(new LineEdit(c.FilePath, c.StartLine ?? end, end, c.LineText, suggestion.Lines));
        }

        var authors = comments.Select(c => c.Author).DistinctBy(u => u.Id).ToList();
        var message = (comments.Count == 1 ? "Apply suggestion" : $"Apply {comments.Count} suggestions") + " from "
                      + string.Join(", ", authors.Select(u => "@" + u.UserName));
        var trailers = authors.Where(u => u.Id != actor.Id).Select(u => RepositoryService.IdentityOf(u))
            .Select(i => $"Co-authored-by: {i.Name} <{i.Email}>").ToList();
        if (trailers.Count > 0) message += "\n\n" + string.Join("\n", trailers);

        var me = RepositoryService.IdentityOf(actor);
        var (before, sha) = await repos.Git(pr.Repository, path =>
        {
            var tip = GitOps.ResolveSha(path, pr.SourceBranch);
            return (tip, GitOps.ApplyLineEdits(path, pr.SourceBranch, edits, message, me, me, tip));
        });

        var now = DateTime.UtcNow;
        var rootIds = comments.Select(c => c.ParentId ?? c.Id).ToHashSet();
        foreach (var c in comments) c.AppliedCommitSha = sha;
        foreach (var root in await db.PullRequestComments.Where(c => rootIds.Contains(c.Id) && !c.IsResolved).ToListAsync())
        {
            root.IsResolved = true;
            root.ResolvedById = actor.Id;
            root.ResolvedAt = now;
        }
        Log(db, pr, actor.Id, $"applied {(comments.Count == 1 ? "a suggestion" : comments.Count + " suggestions")} in commit `{sha[..7]}`");
        await db.SaveChangesAsync();
        await pushEvents.SendAsync(pr.RepositoryId, [Webhooks.WebhookPushEvents.Branch(pr.SourceBranch, before, sha)], actor.Id);
        await notifications.OnPullRequestCommentChangedAsync(prId);
        return sha;
    }

    // ---------- Code owners ----------

    /// <summary>CODEOWNERS owners (other than the author) of the files a new PR changes, read from its target branch.</summary>
    private async Task<IEnumerable<int>> CodeOwnerReviewersAsync(Repository repo, string target, IEnumerable<FileDiff> files, int authorId)
    {
        var paths = files.SelectMany(f => f.OldPath != null ? new[] { f.Path, f.OldPath } : [f.Path]);
        var report = await codeOwners.ForPathsAsync(repo, target, paths);
        return report.OwnerIds.Where(id => id != authorId).ToList();
    }

    // ---------- Merge strategies ----------

    /// <summary>"Title (#12)", a list of the commit subjects (oldest first) when there are several.</summary>
    public static string DefaultSquashMessage(PullRequest pr, IReadOnlyList<CommitInfo>? commits)
    {
        var message = $"{pr.Title} (#{pr.Number})";
        var list = (commits ?? []).Reverse().ToList();
        if (list.Count > 1) message += "\n\n" + string.Join("\n", list.Select(c => "* " + c.MessageShort));
        return message;
    }

    /// <summary>Co-authored-by trailers for the authors of <paramref name="commits"/> other than <paramref name="author"/>, by e-mail.</summary>
    public static List<string> CoAuthorTrailers(IEnumerable<CommitInfo>? commits, GitIdentity author) =>
        (commits ?? []).Reverse()
            .Where(c => !string.Equals(c.AuthorEmail, author.Email, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.AuthorEmail.ToLowerInvariant())
            .Select(c => $"Co-authored-by: {c.AuthorName} <{c.AuthorEmail}>").ToList();

    private async Task<MergeOutcome> RunMergeAsync(PullRequest pr, User actor, MergeRequest request, CompareResult? before)
    {
        var who = RepositoryService.IdentityOf(actor);
        switch (request.Strategy)
        {
            case MergeStrategy.Squash:
            {
                // The PR's author authors the squash commit; whoever merges commits it; everyone else is a co-author.
                await using var db = await dbFactory.CreateDbContextAsync();
                var author = await db.Users.AsNoTracking().FirstAsync(u => u.Id == pr.AuthorId);
                var authorId = RepositoryService.IdentityOf(author);
                var message = string.IsNullOrWhiteSpace(request.CommitMessage) ? DefaultSquashMessage(pr, before?.Commits) : request.CommitMessage.Trim();
                var trailers = CoAuthorTrailers(before?.Commits, authorId)
                    .Where(t => !message.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
                trailers.Add("Merge-Strategy: squash");
                message += "\n\n" + string.Join("\n", trailers);
                return await repos.Git(pr.Repository, path => GitOps.Squash(path, pr.SourceBranch, pr.TargetBranch, message, authorId, who));
            }
            case MergeStrategy.Rebase:
                return await repos.Git(pr.Repository, path => GitOps.Rebase(path, pr.SourceBranch, pr.TargetBranch, who));
            default:
            {
                var message = $"Merge pull request #{pr.Number} from {pr.SourceBranch} into {pr.TargetBranch}\n\n{pr.Title}\n\nMerge-Strategy: merge";
                return await repos.Git(pr.Repository, path =>
                    GitOps.Merge(path, pr.SourceBranch, pr.TargetBranch, message, who, request.AllowFastForward));
            }
        }
    }

    private static string MergedActivity(MergeStrategy strategy, MergeOutcome outcome)
    {
        var sha = outcome.MergeCommitSha![..7];
        return strategy switch
        {
            MergeStrategy.Squash => $"squashed and merged this pull request as commit `{sha}`",
            MergeStrategy.Rebase => $"rebased and merged this pull request (fast-forward to `{sha}`)",
            _ when outcome.FastForwarded => $"merged this pull request (fast-forward to `{sha}`)",
            _ => $"merged this pull request with commit `{sha}`",
        };
    }
}
