using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Releases;

namespace ByteBench.Api;

/// <summary>
/// POST body of <c>releases/notes:generate</c>. <c>tag</c> is an existing tag, or (for a tag that doesn't exist yet) the branch or
/// commit it will be created from; <c>ai</c> picks the AI draft (default) or the plain grouped list.
/// </summary>
public record GenerateReleaseNotesRequest(string Tag, bool Ai = true, string? ExcludeTag = null);

/// <summary>A draft of release notes (never saved). <c>mode</c>: "ai" or "plain".</summary>
public record ReleaseNotesDraftDto(string Markdown, string Mode, string? PreviousTag, int PullRequests, int Commits, int Omitted, string? Notice);

public static class ReleaseNotesEndpoints
{
    public static void MapReleaseNotesEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/repos/{slug}").WithTags("Releases");
        g.MapPost("/releases/notes:generate", Generate).WithName("GenerateReleaseNotes").WithSummary("Draft release notes")
            .WithDescription("Needs Developer+ (as for creating a release). Groups the merged pull requests and commits since the previous release into Breaking changes / Features / Fixes / Other. " +
                             "`ai: true` (default) asks the configured AI (422 with the reason when AI is off or the hourly limit is reached); `ai: false` builds the plain list from labels. " +
                             "The draft is returned only; nothing is saved.")
            .WithJsonBody<GenerateReleaseNotesRequest>();
    }

    private static async Task<ReleaseNotesDraftDto> Generate(HttpContext http, RepositoryService repos, ReleaseNotesService notes, ApiCaller caller, string key, string slug)
    {
        var repo = await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");
        var body = await ApiResults.ReadBodyAsync<GenerateReleaseNotesRequest>(http.Request);
        if (string.IsNullOrWhiteSpace(body.Tag)) throw ApiException.Unprocessable("tag is required.");
        try
        {
            var draft = body.Ai
                ? await notes.GenerateWithAiAsync(repo, body.Tag.Trim(), body.ExcludeTag, caller.Id, http.RequestAborted)
                : await notes.GeneratePlainAsync(repo, body.Tag.Trim(), body.ExcludeTag, caller.Id);
            return new ReleaseNotesDraftDto(draft.Markdown, draft.Mode, draft.PreviousTag, draft.PullRequests, draft.Commits, draft.Omitted, draft.Notice);
        }
        catch (ForbiddenException) { throw; }
        catch (InvalidOperationException ex) { throw ApiException.Unprocessable(ex.Message); }
    }
}
