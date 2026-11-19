using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Ai;

/// <summary>
/// AI features for issues: drafting, duplicate hints, "catch me up" summaries, natural-language search and
/// release notes. Prompt building and response mapping live in <see cref="IssueAiLogic"/> (pure functions);
/// this class only gathers data, calls Claude through <see cref="AiClient"/> and records usage.
/// Nothing here writes to the database except usage rows: every result is returned for a human to apply.
/// </summary>
public class IssueAiService(AiClient ai, IssueService issues, PullRequestService prs, ILogger<IssueAiService> log)
{
    private const Effort LowEffort = Effort.Low;

    /// <summary>Summaries per issue id, valid while the issue's UpdatedAt is unchanged.</summary>
    private static readonly ConcurrentDictionary<int, CachedSummary> SummaryCache = new();

    public record CachedSummary(DateTime IssueUpdatedAt, string Markdown, DateTime GeneratedAt);

    /// <summary>False when no API key is configured; every AI control hides itself.</summary>
    public virtual bool IsConfigured => ai.IsConfigured;

    // ---------------- 1. Draft ----------------

    public async Task<AiOutcome<IssueDraft>> DraftIssueAsync(Project project, string roughText, int? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roughText)) return AiOutcome<IssueDraft>.Fail("Describe the issue first.");
        var labels = project.Labels.Select(l => new NamedId(l.Id, l.Name)).ToList();
        var prompt = IssueAiLogic.BuildDraftPrompt(project.Key, project.Name, labels.Select(l => l.Name), roughText);
        var result = await Safe(() => CompleteJsonAsync("issue-draft", prompt, IssueAiLogic.DraftSchema, LowEffort, 4000, userId, project.Id, ct));
        if (result.Error != null) return AiOutcome<IssueDraft>.Fail(result.Error);
        var parsed = IssueAiLogic.ParseJson<DraftDto>(result.Value!);
        return parsed.Ok ? IssueAiLogic.MapDraft(parsed.Value, labels) : AiOutcome<IssueDraft>.Fail(parsed.Error!);
    }

    // ---------------- 2. Duplicates ----------------

    /// <summary>Keyword prefilter over open issues (no AI call). Public so the UI and tests can use it directly.</summary>
    public async Task<List<DuplicateCandidate>> DuplicateCandidatesAsync(int projectId, string title, string? description)
    {
        var open = await issues.GetIssuesAsync(projectId, new IssueFilter { Open = true });
        return IssueAiLogic.RankCandidates(title, description, open);
    }

    public async Task<AiOutcome<List<DuplicateHint>>> FindDuplicatesAsync(Project project, string title, string? description,
        int? userId, CancellationToken ct = default)
    {
        if (IssueAiLogic.WordCount(title) < 4) return AiOutcome<List<DuplicateHint>>.Success([]);
        var candidates = await DuplicateCandidatesAsync(project.Id, title, description);
        if (candidates.Count == 0) return AiOutcome<List<DuplicateHint>>.Success([]); // skip the AI call

        var prompt = IssueAiLogic.BuildDuplicatesPrompt(project.Key, title, description, candidates);
        var result = await Safe(() => CompleteJsonAsync("issue-duplicates", prompt, IssueAiLogic.DuplicatesSchema, LowEffort, 3000, userId, project.Id, ct));
        if (result.Error != null) return AiOutcome<List<DuplicateHint>>.Fail(result.Error);
        var parsed = IssueAiLogic.ParseJson<DuplicatesDto>(result.Value!);
        return parsed.Ok
            ? AiOutcome<List<DuplicateHint>>.Success(IssueAiLogic.MapDuplicates(parsed.Value, project.Key, candidates))
            : AiOutcome<List<DuplicateHint>>.Fail(parsed.Error!);
    }

    // ---------------- 3. Catch me up ----------------

    public CachedSummary? GetCachedSummary(Issue issue) =>
        SummaryCache.TryGetValue(issue.Id, out var c) && c.IssueUpdatedAt == issue.UpdatedAt ? c : null;

    /// <summary>
    /// Streams a summary. Yields text deltas, then one final event with Done=true; if the answer ended abnormally
    /// (refusal, truncation, error) that final event's Text carries a note. Successful results are cached.
    /// </summary>
    public async IAsyncEnumerable<AiStreamEvent> StreamSummaryAsync(Issue issue, int? userId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prompt = IssueAiLogic.BuildSummaryPrompt(issue);
        var text = new System.Text.StringBuilder();
        string? stop = null, error = null;

        await using var e = StreamTextAsync("issue-summary", prompt, Effort.Medium, 3000, userId, issue.ProjectId, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            AiStreamEvent ev;
            try
            {
                if (!await e.MoveNextAsync()) break;
                ev = e.Current;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "Issue summary stream failed");
                error = "The AI request failed. Please try again.";
                break;
            }
            if (ev.Done) { stop = ev.StopReason; break; }
            if (ev.Text is { Length: > 0 } t) { text.Append(t); yield return new AiStreamEvent(t); }
        }

        var note = error ?? IssueAiLogic.StreamEndNote(stop, text.Length > 0);
        if (note == null)
            SummaryCache[issue.Id] = new CachedSummary(issue.UpdatedAt, text.ToString().Trim(), DateTime.UtcNow);
        if (stop == "refusal") log.LogWarning("Claude declined an issue-summary request");
        yield return new AiStreamEvent(note, stop, Done: true);
    }

    // ---------------- 4. Natural-language search ----------------

    public async Task<AiOutcome<SearchInterpretation>> InterpretSearchAsync(Project project, IReadOnlyList<User> users, int meId,
        string query, string currentSort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return AiOutcome<SearchInterpretation>.Fail("Type what you are looking for.");
        var ctx = new SearchContext(meId,
            users.Select(u => new NamedId(u.Id, u.DisplayName)).Concat(users.Where(u => u.UserName != null).Select(u => new NamedId(u.Id, u.UserName!))).ToList(),
            project.Labels.Select(l => new NamedId(l.Id, l.Name)).ToList(),
            project.Milestones.Select(m => new NamedId(m.Id, m.Title)).ToList());
        var prompt = IssueAiLogic.BuildSearchPrompt(ctx with { Users = users.Select(u => new NamedId(u.Id, u.DisplayName)).ToList() }, query);
        var result = await Safe(() => CompleteJsonAsync("issue-search", prompt, IssueAiLogic.SearchSchema, LowEffort, 2000, meId, project.Id, ct));
        if (result.Error != null) return AiOutcome<SearchInterpretation>.Fail(result.Error);
        var parsed = IssueAiLogic.ParseJson<SearchDto>(result.Value!);
        return parsed.Ok
            ? AiOutcome<SearchInterpretation>.Success(IssueAiLogic.MapSearch(parsed.Value, ctx, currentSort))
            : AiOutcome<SearchInterpretation>.Fail(parsed.Error!);
    }

    // ---------------- 5. Release notes ----------------

    public async Task<AiOutcome<string>> ReleaseNotesAsync(Project project, Milestone milestone, int? userId, CancellationToken ct = default)
    {
        var closed = await issues.GetIssuesAsync(project.Id, new IssueFilter { Open = false, MilestoneId = milestone.Id, Sort = "number" });
        if (closed.Count == 0) return AiOutcome<string>.Fail("This milestone has no closed issues yet.");

        var numbers = closed.Select(i => i.Number).ToHashSet();
        var merged = new List<IssueAiLogic.ReleasePr>();
        try
        {
            foreach (var pr in await prs.ListAsync(project.Id, PullRequestState.Merged))
            {
                var refs = IssueKeys.Numbers(project.Key, [pr.Title, pr.SourceBranch, pr.Description]).Where(numbers.Contains).ToList();
                if (refs.Count > 0)
                    merged.Add(new($"{pr.Repository.Slug}#{pr.Number}", pr.Title, refs.Select(n => $"{project.Key}-{n}")));
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not load merged pull requests for release notes"); }

        var prompt = IssueAiLogic.BuildReleaseNotesPrompt(project.Name, milestone.Title,
            closed.Select(i => new IssueAiLogic.ReleaseIssue(i.Key, i.Title, i.Type, i.Labels.Select(l => l.Name))), merged);
        var result = await Safe(() => CompleteTextAsync("release-notes", prompt, LowEffort, 4000, userId, project.Id, ct));
        return result.Error != null ? AiOutcome<string>.Fail(result.Error) : IssueAiLogic.ParseMarkdown(result.Value!);
    }

    // ================= Transport (virtual so a fake responder can stand in during UI testing) =================

    /// <summary>Plain-text completion through the shared client (cached system prompt, refusal fallback, usage).</summary>
    protected virtual Task<AiResult> CompleteTextAsync(string feature, string user, Effort effort, int maxTokens,
        int? userId, int? projectId, CancellationToken ct) =>
        ai.CompleteAsync(feature, IssueAiLogic.SystemPrompt, user, effort, maxTokens, userId, projectId, ct);

    /// <summary>
    /// Structured-output completion. Private equivalent of a generic CompleteJsonAsync helper; the integrator may
    /// replace it with a shared one in AiClient.Structured.cs.
    /// </summary>
    protected virtual async Task<AiResult> CompleteJsonAsync(string feature, string user, string schemaJson, Effort effort,
        int maxTokens, int? userId, int? projectId, CancellationToken ct)
    {
        var response = await ai.Client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = ai.Model,
            MaxTokens = maxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = effort, Format = new BetaJsonOutputFormat { Schema = SchemaOf(schemaJson) } },
            System = SystemBlocks(),
            Messages = [new BetaUserMessageParam { Content = user }],
        }, ct);

        await ai.RecordUsageAsync(feature, userId, projectId,
            response.Usage.InputTokens, response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0, ct);

        var stop = response.StopReason?.Raw();
        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        if (stop == "refusal") log.LogWarning("Claude declined a {Feature} request", feature);
        return new AiResult(text, stop, stop == "refusal");
    }

    /// <summary>Streams text deltas, then a Done event carrying the stop reason. Records usage when the stream ends.</summary>
    protected virtual async IAsyncEnumerable<AiStreamEvent> StreamTextAsync(string feature, string user, Effort effort, int maxTokens,
        int? userId, int? projectId, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = ai.Model,
            MaxTokens = maxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = effort },
            System = SystemBlocks(),
            Messages = [new BetaUserMessageParam { Content = user }],
        };

        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        string? stop = null;
        var started = false;
        try
        {
            await foreach (var ev in ai.Client.Beta.Messages.CreateStreaming(parameters, ct))
            {
                if (ev.TryPickStart(out var start))
                {
                    started = true;
                    input = start.Message.Usage.InputTokens;
                    cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                    cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
                }
                else if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t))
                {
                    yield return new AiStreamEvent(t.Text);
                }
                else if (ev.TryPickDelta(out var md))
                {
                    output = md.Usage.OutputTokens;
                    stop = md.Delta.StopReason?.Raw() ?? stop;
                }
            }
        }
        finally
        {
            if (started)
                await ai.RecordUsageAsync(feature, userId, projectId, input, output, cacheRead, cacheWrite, CancellationToken.None);
        }
        yield return new AiStreamEvent(null, stop, Done: true);
    }

    private static List<BetaTextBlockParam> SystemBlocks() =>
        [new() { Text = IssueAiLogic.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() }];

    private static readonly ConcurrentDictionary<string, Dictionary<string, JsonElement>> Schemas = new();

    private static Dictionary<string, JsonElement> SchemaOf(string json) => Schemas.GetOrAdd(json, j =>
    {
        using var doc = JsonDocument.Parse(j);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    });

    /// <summary>Runs a call, turning API/network failures into a user-facing error.</summary>
    private async Task<AiOutcome<AiResult>> Safe(Func<Task<AiResult>> call)
    {
        try { return AiOutcome<AiResult>.Success(await call()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "AI request failed");
            return AiOutcome<AiResult>.Fail("The AI request failed. Please try again in a moment.");
        }
    }
}
