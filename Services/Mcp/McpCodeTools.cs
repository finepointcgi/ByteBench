using System.Text;
using ByteBench.Data;
using ByteBench.Services.Ci;
using ByteBench.Services.Code;
using ByteBench.Services.Git;
using static ByteBench.Services.Mcp.McpSchema;

namespace ByteBench.Services.Mcp;

/// <summary>Tools for repositories, pull requests, code search and CI pipelines (read-only).</summary>
internal static class McpCodeTools
{
    private static readonly string[] PrStates = ["open", "merged", "declined", "all"];

    public static IEnumerable<McpToolDef> All()
    {
        yield return new("list_repositories", "List repositories",
            "List the Git repositories you can read, as PROJECT/slug (the form the other code tools take), with default branch and open pull request count.",
            Obj(null, ("project", Str("Only repositories of this project key.", 32))), true, false, true, ListRepositories);

        yield return new("list_pull_requests", "List pull requests",
            "List pull requests of a repository or of a whole project, newest activity first. Give `repository` (PROJECT/slug) or `project`.",
            Obj(null,
                ("repository", Str("Repository as PROJECT/slug, from list_repositories.", 140)),
                ("project", Str("Project key, to list across its repositories.", 32)),
                ("state", Str("open (default), merged, declined or all.", 10, values: PrStates)),
                ("limit", Int("How many to return (default 20).", 1, 50))),
            true, false, true, ListPullRequests);

        yield return new("get_pull_request", "Get a pull request",
            "Get one pull request: description, state, reviews and review state, whether it merges cleanly, recent comments and a diff summary (files with added/deleted line counts). " +
            "Set `include_diff` to also get the unified diff, cut at a size limit (marked truncated).",
            Obj(["repository", "number"],
                ("repository", Str("Repository as PROJECT/slug.", 140)),
                ("number", Int("Pull request number within the repository.", 1, 10_000_000)),
                ("include_diff", Bool("Include the unified diff text (size-capped). Default false."))),
            true, false, true, GetPullRequest);

        yield return new("read_file", "Read a file",
            "Read a text file from a repository at a branch, tag or commit. Output is capped (about 20,000 characters; use start_line to read further); binary files are refused.",
            Obj(["repository", "path"],
                ("repository", Str("Repository as PROJECT/slug.", 140)),
                ("path", Str("File path inside the repository, e.g. src/Program.cs.", 500, minLength: 1)),
                ("ref", Str("Branch, tag or commit (default: the repository's default branch).", 200)),
                ("start_line", Int("First line to return, 1-based (default 1).", 1, 10_000_000))),
            true, false, true, ReadFile);

        yield return new("list_directory", "List a directory",
            "List the files and folders in a directory of a repository (folders first). Leave `path` empty for the repository root.",
            Obj(["repository"],
                ("repository", Str("Repository as PROJECT/slug.", 140)),
                ("path", Str("Directory path, empty for the root.", 500)),
                ("ref", Str("Branch, tag or commit (default: the default branch).", 200))),
            true, false, true, ListDirectory);

        yield return new("search_code", "Search code",
            "Search file contents (git grep) and file names in a repository's default branch or a given ref. Give `repository`, or `project` to search all of its repositories. Returns matching lines with file and line number.",
            Obj(["query"],
                ("query", Str("Text to find (or a regular expression with regex=true), at most 200 characters.", 200, minLength: 1)),
                ("repository", Str("Repository as PROJECT/slug.", 140)),
                ("project", Str("Project key, to search all of its repositories (default branches).", 32)),
                ("ref", Str("Branch, tag or commit; only with `repository`.", 200)),
                ("regex", Bool("Treat query as a regular expression. Default false.")),
                ("case_sensitive", Bool("Match case. Default false.")),
                ("limit", Int("Maximum matching lines to return (default 30).", 1, 100))),
            true, false, true, SearchCode);

        yield return new("list_pipelines", "List CI pipelines",
            "List a repository's CI pipelines, newest first, with status, branch and commit. Use get_pipeline_logs for the output of a run.",
            Obj(["repository"],
                ("repository", Str("Repository as PROJECT/slug.", 140)),
                ("branch", Str("Only pipelines of this branch.", 200)),
                ("status", Str("Only this status.", 10, values: Enum.GetNames<PipelineStatus>().Select(n => n.ToLowerInvariant()).ToArray())),
                ("limit", Int("How many to return (default 10).", 1, 50))),
            true, false, true, ListPipelines);

        yield return new("get_pipeline_logs", "Get pipeline logs",
            "Get the end of a CI step's log. Without `step_id` it picks the first failed step (else the last step) of the pipeline, optionally limited to the job named `job`; the reply lists all jobs and steps so you can ask for another. " +
            "Only the last `tail_chars` characters are returned (default and maximum 16,000), marked truncated when cut. Secrets are masked by the runner.",
            Obj(["repository", "pipeline_id"],
                ("repository", Str("Repository as PROJECT/slug.", 140)),
                ("pipeline_id", Int("Pipeline id from list_pipelines.", 1, int.MaxValue)),
                ("job", Str("Job name, to pick its failed step.", 64)),
                ("step_id", Int("A step id from this tool's step list.", 1, int.MaxValue)),
                ("tail_chars", Int("How many characters from the end of the log (default 16000).", 100, McpLimits.MaxLogChars))),
            true, false, true, GetPipelineLogs);
    }

    private static string RepoName(Repository r) => $"{r.Project.Key}/{r.Slug}";

    private static string PrName(PullRequest p) => $"{RepoName(p.Repository)}#{p.Number}";

    private static async Task<McpToolResult> ListRepositories(McpCall call)
    {
        var repos = call.Get<RepositoryService>();
        List<int> projectIds;
        if (call.Args.Str("project") is { Length: > 0 } key) projectIds = [(await call.RequireProjectAsync(McpArgs.ParseProjectKey(key))).Id];
        else projectIds = (await call.Get<ProjectAccess>().VisibleProjectIdsAsync()).ToList();

        var items = new List<object>();
        var sb = new StringBuilder();
        var total = 0;
        foreach (var projectId in projectIds)
            foreach (var s in await repos.GetRepositoriesAsync(projectId))
            {
                total++;
                if (items.Count >= McpLimits.MaxListItems) continue;
                items.Add(new { repository = RepoName(s.Repository), name = s.Repository.Name, description = McpLimits.Cap(s.Repository.Description, 200), defaultBranch = s.Repository.DefaultBranch, openPullRequests = s.OpenPullRequests });
                sb.AppendLine($"{RepoName(s.Repository)} (default branch {s.Repository.DefaultBranch}, {s.OpenPullRequests} open PRs)");
            }
        var truncated = total > items.Count;
        if (total == 0) sb.AppendLine("No repositories visible.");
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} showing {items.Count} of {total}; pass `project` to narrow.");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new { repositories = items, total, truncated }, truncated);
    }

    private static async Task<McpToolResult> ListPullRequests(McpCall call)
    {
        var limit = call.Args.Int("limit") ?? 20;
        PullRequestState? state = (call.Args.Str("state") ?? "open").ToLowerInvariant() switch
        {
            "all" => null, var s => Enum.Parse<PullRequestState>(s, true),
        };
        List<PullRequest> list;
        if (call.Args.Str("repository") is { Length: > 0 } repoName)
        {
            var repo = await call.RequireRepositoryAsync(repoName);
            list = await call.Get<PullRequestService>().ListAsync(repo.ProjectId, state, repo.Id);
        }
        else if (call.Args.Str("project") is { Length: > 0 } key)
            list = await call.Get<PullRequestService>().ListAsync((await call.RequireProjectAsync(McpArgs.ParseProjectKey(key))).Id, state);
        else throw new McpToolException("Pass `repository` (PROJECT/slug) or `project`.");

        var page = list.Take(limit).ToList();
        var truncated = list.Count > page.Count;
        var sb = new StringBuilder();
        foreach (var p in page)
            sb.AppendLine($"{PrName(p)} [{p.State}{(p.IsDraft ? ", draft" : "")}] {McpLimits.Cap(p.Title, 150)} ({p.SourceBranch} -> {p.TargetBranch}) by {p.Author.UserName}");
        if (page.Count == 0) sb.AppendLine("No pull requests.");
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} showing {page.Count} of {list.Count}; raise `limit` (max 50) or narrow with `repository`.");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new
        {
            pullRequests = page.Select(p => new
            {
                repository = RepoName(p.Repository), number = p.Number, title = p.Title, state = p.State, draft = p.IsDraft,
                source = p.SourceBranch, target = p.TargetBranch, author = p.Author.UserName, updatedAt = Api.Dto.Utc(p.UpdatedAt),
                url = call.BaseUrl + RepoUrls.PullRequest(p),
            }).ToList(),
            total = list.Count, truncated,
        }, truncated);
    }

    private static async Task<McpToolResult> GetPullRequest(McpCall call)
    {
        var repo = await call.RequireRepositoryAsync(call.Args.RequiredStr("repository"));
        var number = call.Args.Int("number") ?? throw new McpToolException("'number' is required.");
        var prs = call.Get<PullRequestService>();
        var pr = await prs.GetAsync(repo.Project.Key, repo.Slug, number) ?? throw new McpToolException($"Pull request {RepoName(repo)}#{number} was not found.");

        var description = McpLimits.Cap(pr.Description, McpLimits.MaxBodyChars, out var descTruncated);
        var reviews = pr.Reviews.OrderBy(r => r.CreatedAt).Select(r => new { user = r.User.UserName, state = r.State }).ToList();
        var approvals = reviews.Count(r => r.state == ReviewState.Approved);
        var changesRequested = reviews.Count(r => r.state == ReviewState.ChangesRequested);
        var comments = pr.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        var shownComments = comments.Skip(Math.Max(0, comments.Count - 10)).ToList();

        CompareResult? changes = null;
        string? mergeState = null;
        try
        {
            changes = await prs.GetChangesAsync(pr);
            if (pr.State == PullRequestState.Open) mergeState = (await prs.CheckMergeAsync(pr)).Check.ToString().ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* branches gone: summary stays empty */ }

        var files = changes?.Files ?? [];
        var shownFiles = files.Take(100).ToList();
        var truncated = descTruncated || files.Count > shownFiles.Count || comments.Count > shownComments.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"{PrName(pr)}: {pr.Title}");
        sb.AppendLine($"State: {pr.State}{(pr.IsDraft ? " (draft)" : "")} | {pr.SourceBranch} -> {pr.TargetBranch} | Author: {pr.Author.UserName}" + (mergeState != null ? $" | Merge check: {mergeState}" : ""));
        sb.AppendLine($"Reviews: {approvals} approved, {changesRequested} changes requested" + (reviews.Count > 0 ? " (" + string.Join(", ", reviews.Select(r => $"{r.user}: {r.state}")) + ")" : ""));
        sb.AppendLine($"URL: {call.BaseUrl}{RepoUrls.PullRequest(pr)}");
        sb.AppendLine().AppendLine("Description:").AppendLine(string.IsNullOrWhiteSpace(description) ? "(none)" : description);
        sb.AppendLine().AppendLine($"Changes: {files.Count} files, +{files.Sum(f => f.Additions)} -{files.Sum(f => f.Deletions)}, {changes?.Commits.Count ?? 0} commits");
        foreach (var f in shownFiles) sb.AppendLine($"  {f.Status} {f.DisplayPath} (+{f.Additions} -{f.Deletions}){(f.IsBinary ? " binary" : f.TooLarge ? " too large to diff" : "")}");
        if (files.Count > shownFiles.Count) sb.AppendLine($"  {McpLimits.TruncatedMarker} {files.Count - shownFiles.Count} more files.");
        if (shownComments.Count > 0)
        {
            sb.AppendLine().AppendLine(comments.Count > shownComments.Count ? $"Comments (last {shownComments.Count} of {comments.Count}):" : "Comments:");
            foreach (var c in shownComments)
                sb.AppendLine($"- {c.Author.UserName}{(c.FilePath != null ? $" on {c.FilePath}:{c.Line ?? c.OldLine}" : "")}: {McpLimits.Cap(c.Body, 1000)}");
        }
        var diffTruncated = false;
        string? diffText = null;
        if (call.Args.Bool("include_diff") == true && changes != null)
        {
            diffText = McpLimits.Cap(RenderDiff(files), McpLimits.MaxDiffChars, out diffTruncated);
            sb.AppendLine().AppendLine("Diff:").AppendLine(diffText);
            truncated |= diffTruncated;
        }
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new
        {
            repository = RepoName(repo), number, title = pr.Title, state = pr.State, draft = pr.IsDraft, source = pr.SourceBranch, target = pr.TargetBranch,
            author = pr.Author.UserName, description, reviews, approvals, changesRequested, mergeState,
            files = shownFiles.Select(f => new { path = f.Path, status = f.Status, additions = f.Additions, deletions = f.Deletions, binary = f.IsBinary }).ToList(),
            totalFiles = files.Count, commits = changes?.Commits.Count ?? 0,
            comments = shownComments.Select(c => new { author = c.Author.UserName, path = c.FilePath, line = c.Line ?? c.OldLine, body = McpLimits.Cap(c.Body, 1000) }).ToList(),
            totalComments = comments.Count, diff = diffText, diffTruncated, truncated, url = call.BaseUrl + RepoUrls.PullRequest(pr),
        }, truncated);
    }

    private static string RenderDiff(IReadOnlyList<FileDiff> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            sb.AppendLine($"--- {f.OldPath ?? f.Path}").AppendLine($"+++ {f.Path}");
            if (f.IsBinary) { sb.AppendLine("(binary file)"); continue; }
            if (f.TooLarge) { sb.AppendLine("(diff too large)"); continue; }
            foreach (var h in f.Hunks)
            {
                sb.AppendLine(h.Header);
                foreach (var l in h.Lines)
                    sb.Append(l.Kind switch { DiffLineKind.Added => '+', DiffLineKind.Deleted => '-', _ => ' ' }).AppendLine(l.Text);
            }
            if (sb.Length > McpLimits.MaxDiffChars * 2) break; // enough: the cap below cuts it exactly
        }
        return sb.ToString();
    }

    private static async Task<McpToolResult> ReadFile(McpCall call)
    {
        var repo = await call.RequireRepositoryAsync(call.Args.RequiredStr("repository"));
        var path = McpArgs.SafePath(call.Args.RequiredStr("path"), "path");
        if (path.Length == 0) throw new McpToolException("'path' must name a file.");
        var rev = call.Args.Str("ref") is { Length: > 0 } r ? McpArgs.SafeRef(r) : repo.DefaultBranch;
        var repos = call.Get<RepositoryService>();
        var blob = await repos.Git(repo, p => GitOps.Exists(p) ? GitOps.GetBlob(p, rev, path) : null)
                   ?? throw new McpToolException($"File '{path}' was not found at '{rev}' in {RepoName(repo)}. Use list_directory to look around.");
        if (blob.IsBinary) throw new McpToolException($"'{path}' is a binary file ({blob.Size} bytes); only text files can be read.");
        if (blob.TooLarge || blob.Text == null) throw new McpToolException($"'{path}' is too large to read ({blob.Size} bytes; the limit is {GitOps.MaxBlobBytes}).");

        var lines = blob.Text.Split('\n');
        var start = Math.Max(1, call.Args.Int("start_line") ?? 1);
        if (start > lines.Length) throw new McpToolException($"'{path}' has only {lines.Length} lines.");
        var text = string.Join('\n', lines.Skip(start - 1));
        var content = McpLimits.Cap(text, McpLimits.MaxFileChars, out var truncated);
        var shownLines = content.Count(c => c == '\n') + 1;
        var header = $"{RepoName(repo)}:{path} @ {rev} ({blob.Size} bytes, {lines.Length} lines)" + (start > 1 ? $", from line {start}" : "");
        var note = truncated ? $"\n{McpLimits.TruncatedMarker} continue with start_line={start + shownLines - 1}." : "";
        return McpToolResult.Ok(header + "\n" + content + note, new { repository = RepoName(repo), path, @ref = rev, size = blob.Size, totalLines = lines.Length, startLine = start, content, truncated }, truncated);
    }

    private static async Task<McpToolResult> ListDirectory(McpCall call)
    {
        var repo = await call.RequireRepositoryAsync(call.Args.RequiredStr("repository"));
        var dir = McpArgs.SafePath(call.Args.Str("path") ?? "", "path");
        var rev = call.Args.Str("ref") is { Length: > 0 } r ? McpArgs.SafeRef(r) : repo.DefaultBranch;
        var repos = call.Get<RepositoryService>();
        var items = await repos.Git(repo, p => GitOps.Exists(p) && GitOps.PathKind(p, rev, dir) == "tree" ? GitOps.ListTree(p, rev, dir, withLastCommit: false) : null)
                    ?? throw new McpToolException($"Directory '{(dir.Length == 0 ? "/" : dir)}' was not found at '{rev}' in {RepoName(repo)} (or it is a file; use read_file).");
        var shown = items.Take(200).ToList();
        var truncated = items.Count > shown.Count;
        var sb = new StringBuilder($"{RepoName(repo)}:{(dir.Length == 0 ? "/" : dir)} @ {rev}\n");
        foreach (var i in shown) sb.AppendLine(i.IsDirectory ? i.Name + "/" : i.IsSubmodule ? i.Name + " (submodule)" : $"{i.Name} ({i.Size} bytes)");
        if (shown.Count == 0) sb.AppendLine("(empty)");
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} showing {shown.Count} of {items.Count} entries.");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new
        {
            repository = RepoName(repo), path = dir, @ref = rev,
            entries = shown.Select(i => new { name = i.Name, path = i.Path, type = i.IsDirectory ? "directory" : i.IsSubmodule ? "submodule" : "file", size = i.Size }).ToList(),
            total = items.Count, truncated,
        }, truncated);
    }

    private static async Task<McpToolResult> SearchCode(McpCall call)
    {
        var query = call.Args.RequiredStr("query");
        var limit = call.Args.Int("limit") ?? 30;
        var options = new CodeSearchOptions(query, IgnoreCase: call.Args.Bool("case_sensitive") != true, Regex: call.Args.Bool("regex") == true);
        var search = call.Get<CodeSearchService>();
        List<RepoSearchResult> results;
        if (call.Args.Str("repository") is { Length: > 0 } repoName)
        {
            var repo = await call.RequireRepositoryAsync(repoName);
            var rev = call.Args.Str("ref") is { Length: > 0 } r ? McpArgs.SafeRef(r) : null;
            results = [await search.SearchRepositoryAsync(repo, rev, options, Math.Min(limit, CodeSearchService.MaxMatchesPerRepo), call.Ct)];
        }
        else if (call.Args.Str("project") is { Length: > 0 } key)
            results = await search.SearchProjectAsync((await call.RequireProjectAsync(McpArgs.ParseProjectKey(key))).Id, options, call.Ct);
        else throw new McpToolException("Pass `repository` (PROJECT/slug) or `project`.");

        if (results.FirstOrDefault(r => r.Error != null) is { } failed && results.All(r => r.Error != null))
            throw new McpToolException(failed.Error!);

        var matches = new List<object>();
        var sb = new StringBuilder();
        var count = 0;
        var truncated = results.Any(r => r.Truncated);
        foreach (var res in results.Where(r => r.Error == null))
        {
            foreach (var name in res.FileNames.Take(10)) sb.AppendLine($"[file name] {RepoName(res.Repository)}:{name}");
            foreach (var file in res.Files)
                foreach (var snippet in file.Snippets)
                    foreach (var line in snippet.Where(l => l.IsMatch))
                    {
                        if (count >= limit) { truncated = true; break; }
                        count++;
                        var text = McpLimits.Cap(line.Text, 300);
                        matches.Add(new { repository = RepoName(res.Repository), path = file.Path, line = line.Number, text });
                        sb.AppendLine($"{RepoName(res.Repository)}:{file.Path}:{line.Number}: {text}");
                    }
        }
        if (count == 0 && sb.Length == 0) sb.AppendLine("No matches.");
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} more matches exist; refine the query or raise `limit`.");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new { matches, truncated }, truncated);
    }

    private static async Task<McpToolResult> ListPipelines(McpCall call)
    {
        var repo = await call.RequireRepositoryAsync(call.Args.RequiredStr("repository"));
        PipelineStatus? status = call.Args.Str("status") is { } s ? Enum.Parse<PipelineStatus>(s, true) : null;
        var limit = call.Args.Int("limit") ?? 10;
        var branch = call.Args.Str("branch") is { Length: > 0 } b ? McpArgs.SafeRef(b) : null;
        var page = await call.Get<PipelineService>().ListAsync(repo, 1, limit, branch, status);
        var sb = new StringBuilder();
        foreach (var p in page.Items)
            sb.AppendLine($"#{p.Id} [{p.Status}] {p.Ref} {p.CommitSha[..Math.Min(8, p.CommitSha.Length)]} {McpLimits.Cap(p.CommitMessage?.Split('\n')[0], 100)} ({p.Trigger})");
        if (page.Items.Count == 0) sb.AppendLine("No pipelines.");
        var truncated = page.Total > page.Items.Count;
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} showing {page.Items.Count} of {page.Total}.");
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new
        {
            pipelines = page.Items.Select(p => new
            {
                id = p.Id, status = p.Status, @ref = p.Ref, commit = p.CommitSha, message = McpLimits.Cap(p.CommitMessage?.Split('\n')[0], 200), trigger = p.Trigger,
                createdAt = Api.Dto.Utc(p.CreatedAt), finishedAt = Api.Dto.Utc(p.FinishedAt),
            }).ToList(),
            total = page.Total, truncated,
        }, truncated);
    }

    private static async Task<McpToolResult> GetPipelineLogs(McpCall call)
    {
        var repo = await call.RequireRepositoryAsync(call.Args.RequiredStr("repository"));
        var id = call.Args.Int("pipeline_id") ?? throw new McpToolException("'pipeline_id' is required.");
        var tail = call.Args.Int("tail_chars") ?? McpLimits.MaxLogChars;
        var pipelines = call.Get<PipelineService>();
        var pipeline = await pipelines.GetAsync(repo, id) ?? throw new McpToolException($"Pipeline {id} was not found in {RepoName(repo)}. Use list_pipelines.");
        var jobs = pipeline.Jobs.OrderBy(j => j.Order).ToList();
        if (call.Args.Str("job") is { Length: > 0 } jobName)
            jobs = jobs.Where(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase)).ToList();
        var steps = jobs.SelectMany(j => j.Steps.OrderBy(s => s.Order).Select(s => (Job: j, Step: s))).ToList();
        if (steps.Count == 0) throw new McpToolException("No matching job or steps in this pipeline.");

        (PipelineJob Job, PipelineStep Step) chosen;
        if (call.Args.Int("step_id") is { } stepId)
            chosen = pipeline.Jobs.SelectMany(j => j.Steps.Select(s => (Job: j, Step: s))).FirstOrDefault(x => x.Step.Id == stepId) is { Step: not null } f ? f
                : throw new McpToolException($"Step {stepId} is not part of pipeline {id}.");
        else
            chosen = steps.FirstOrDefault(x => x.Step.Status is PipelineStatus.Failed or PipelineStatus.Error) is { Step: not null } failed ? failed : steps[^1];

        var log = await pipelines.GetLogAsync(repo, id, chosen.Step.Id);
        var text = log?.Text ?? "";
        var cutHead = text.Length > tail;
        var shown = cutHead ? text[^tail..] : text;
        if (cutHead && shown.Length > 0 && char.IsLowSurrogate(shown[0])) shown = shown[1..];
        var truncated = cutHead || log?.Truncated == true;

        var sb = new StringBuilder();
        sb.AppendLine($"Pipeline #{pipeline.Id} [{pipeline.Status}] {pipeline.Ref}. Step {chosen.Step.Id} \"{chosen.Step.Name}\" of job \"{chosen.Job.Name}\" [{chosen.Step.Status}{(chosen.Step.ExitCode is { } code ? $", exit {code}" : "")}]");
        sb.AppendLine("Steps: " + string.Join("; ", pipeline.Jobs.OrderBy(j => j.Order).SelectMany(j => j.Steps.OrderBy(s => s.Order).Select(s => $"{s.Id}={j.Name}/{s.Name} [{s.Status}]")).Take(60)));
        sb.AppendLine(cutHead ? $"--- log (last {shown.Length} of {text.Length} characters) ---" : "--- log ---");
        sb.AppendLine(shown.Length == 0 ? "(empty)" : shown);
        if (truncated) sb.AppendLine($"{McpLimits.TruncatedMarker} {(cutHead ? "the start of the log is omitted. " : "")}{(log?.Truncated == true ? "the runner cut this log at its size limit." : "")}".TrimEnd());
        return McpToolResult.Ok(sb.ToString().TrimEnd(), new
        {
            pipelineId = pipeline.Id, status = pipeline.Status, stepId = chosen.Step.Id, step = chosen.Step.Name, job = chosen.Job.Name, stepStatus = chosen.Step.Status,
            exitCode = chosen.Step.ExitCode, log = shown, totalLogLength = text.Length, truncated,
            steps = pipeline.Jobs.SelectMany(j => j.Steps.Select(s => new { id = s.Id, job = j.Name, name = s.Name, status = s.Status })).Take(60).ToList(),
        }, truncated);
    }
}
