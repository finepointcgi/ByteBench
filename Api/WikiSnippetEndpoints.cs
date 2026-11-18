using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Git;
using GitBench.Services.Snippets;
using GitBench.Services.Wiki;

namespace GitBench.Api;

// ----- Wiki & snippets wire types -----

public record WikiPageSummaryDto(string Path, string Title, string WebUrl);

/// <summary><c>sha</c> is the page's blob id: send it back as <c>sha</c> when updating so a concurrent edit is a 409, not an overwrite.</summary>
public record WikiPageDto(string Path, string Title, string Content, string Sha, CommitDto? LastCommit, string WebUrl);

public record CreateWikiPageRequest(string? Path, string? Content, string? Message = null);

/// <summary><c>newPath</c> renames/moves the page. Without <c>sha</c> the current version is overwritten.</summary>
public record UpdateWikiPageRequest(string? Content, string? Sha = null, string? Message = null, string? NewPath = null);

public record SnippetFileDto(string FileName, string Content, string RawUrl);

public record SnippetSummaryDto(int Id, string Title, SnippetVisibility Visibility, UserDto Author, string? Project, IReadOnlyList<string> Files,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string WebUrl);

public record SnippetDto(int Id, string Title, string? Description, SnippetVisibility Visibility, UserDto Author, string? Project,
    int Revision, int CurrentRevision, IReadOnlyList<SnippetFileDto> Files, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string WebUrl, string EmbedUrl);

public record SnippetFileRequest(string? FileName, string? Content);

/// <summary><c>project</c> (a project key) makes a project snippet; visibility is private/public (personal) or private/project (project).</summary>
public record SaveSnippetRequest(string? Title, string? Description, SnippetVisibility? Visibility, IReadOnlyList<SnippetFileRequest>? Files, string? Project = null);

public record SnippetCommentDto(int Id, UserDto Author, string Body, DateTimeOffset CreatedAt);

public record CreateSnippetCommentRequest(string? Body);

/// <summary>/api/v1/projects/{key}/wiki/… and /api/v1/snippets/….</summary>
public static class WikiSnippetEndpoints
{
    public static void MapWikiSnippetEndpoints(this RouteGroupBuilder api)
    {
        var w = api.MapGroup("/projects/{key}/wiki").WithTags("Wiki");
        w.MapGet("/pages", ListPages).WithName("ListWikiPages").WithSummary("List wiki pages");
        w.MapGet("/pages/{**path}", GetPage).WithName("GetWikiPage").WithSummary("Get a wiki page")
            .WithDescription("?rev= reads the page at a commit. Paths use / for folders and no .md, e.g. guides/Setup.");
        w.MapPost("/pages", CreatePage).WithName("CreateWikiPage").WithSummary("Create a wiki page")
            .WithDescription("Needs Developer+ and the wiki turned on. Each save is a commit by you. 409 if the page exists.")
            .WithJsonBody<CreateWikiPageRequest>().Produces<WikiPageDto>(StatusCodes.Status201Created);
        w.MapPut("/pages/{**path}", UpdatePage).WithName("UpdateWikiPage").WithSummary("Update, rename or move a wiki page")
            .WithDescription("Send the sha you read; if the page changed since, the answer is 409 Conflict with the current sha.")
            .WithJsonBody<UpdateWikiPageRequest>();
        w.MapDelete("/pages/{**path}", DeletePage).WithName("DeleteWikiPage").WithSummary("Delete a wiki page")
            .WithDescription("?sha= makes the delete conditional on the version you read.").Produces(StatusCodes.Status204NoContent);
        w.MapGet("/history", PageHistory).WithName("GetWikiPageHistory").WithSummary("Commits that changed a page (?path=)");

        var s = api.MapGroup("/snippets").WithTags("Snippets");
        s.MapGet("", ListSnippets).WithName("ListSnippets").WithSummary("List snippets")
            .WithDescription("?scope=mine (default: your own) or explore (public personal snippets and project snippets you can see).");
        s.MapGet("/{id:int}", GetSnippet).WithName("GetSnippet").WithSummary("Get a snippet (?rev= for an older revision)");
        s.MapPost("", CreateSnippet).WithName("CreateSnippet").WithSummary("Create a snippet")
            .WithJsonBody<SaveSnippetRequest>().Produces<SnippetDto>(StatusCodes.Status201Created);
        s.MapPut("/{id:int}", UpdateSnippet).WithName("UpdateSnippet").WithSummary("Update a snippet (author only)")
            .WithDescription("Changed files add a revision; title, description and visibility change in place. project is ignored.")
            .WithJsonBody<SaveSnippetRequest>();
        s.MapDelete("/{id:int}", DeleteSnippet).WithName("DeleteSnippet").WithSummary("Delete a snippet").Produces(StatusCodes.Status204NoContent);
        s.MapGet("/{id:int}/files/{fileName}/raw", RawFile).WithName("GetSnippetRawFile").WithSummary("A snippet file as text/plain");
        s.MapGet("/{id:int}/comments", ListComments).WithName("ListSnippetComments").WithSummary("List a snippet's comments");
        s.MapPost("/{id:int}/comments", AddComment).WithName("CreateSnippetComment").WithSummary("Comment on a snippet")
            .WithJsonBody<CreateSnippetCommentRequest>().Produces<SnippetCommentDto>(StatusCodes.Status201Created);
        s.MapDelete("/{id:int}/comments/{commentId:int}", DeleteComment).WithName("DeleteSnippetComment").WithSummary("Delete a comment")
            .Produces(StatusCodes.Status204NoContent);
        api.MapGet("/projects/{key}/snippets", ListProjectSnippets).WithName("ListProjectSnippets").WithSummary("List a project's snippets").WithTags("Snippets");
    }

    // ---------- Wiki ----------

    private static async Task<WikiInfo> RequireWikiAsync(WikiService wikis, string key) =>
        await wikis.GetWikiAsync(key) is { Enabled: true } w ? w : throw ApiException.NotFound($"No wiki for project '{key.ToUpperInvariant()}'.");

    private static string RequirePath(string? path) =>
        WikiPaths.Normalize(path) ?? throw ApiException.Unprocessable("Invalid page path.");

    private static async Task<WikiPageDto> PageDtoAsync(WikiService wikis, WikiInfo w, string path, string baseUrl, string? rev = null) =>
        await wikis.GetPageAsync(w, path, rev) is { } p
            ? new WikiPageDto(p.Path, p.Title, p.Content, p.BlobSha, p.LastCommit is { } c ? Dto.Commit(c) : null, baseUrl + WikiPaths.Url(w.ProjectKey, p.Path))
            : throw ApiException.NotFound($"Wiki page '{path}' not found.");

    private static ApiException Conflict(WikiConflictException ex) =>
        new(StatusCodes.Status409Conflict, "Conflict", ex.Message, new Dictionary<string, object?> { ["currentSha"] = ex.CurrentSha });

    private static async Task<List<WikiPageSummaryDto>> ListPages(HttpContext http, WikiService wikis, PublicBaseUrl url, string key)
    {
        var w = await RequireWikiAsync(wikis, key);
        var baseUrl = url.For(http.Request);
        return (await wikis.ListPagesAsync(w)).Select(p => new WikiPageSummaryDto(p.Path, p.Title, baseUrl + WikiPaths.Url(w.ProjectKey, p.Path))).ToList();
    }

    private static async Task<WikiPageDto> GetPage(HttpContext http, WikiService wikis, PublicBaseUrl url, string key, string path, string? rev) =>
        await PageDtoAsync(wikis, await RequireWikiAsync(wikis, key), RequirePath(path), url.For(http.Request), rev);

    private static async Task<IResult> CreatePage(HttpContext http, WikiService wikis, PublicBaseUrl url, string key)
    {
        var w = await RequireWikiAsync(wikis, key);
        var body = await ApiResults.ReadBodyAsync<CreateWikiPageRequest>(http.Request);
        try
        {
            var saved = await wikis.SavePageAsync(w, null, RequirePath(body.Path), body.Content ?? "", body.Message, null);
            var baseUrl = url.For(http.Request);
            return TypedResults.Created($"{baseUrl}/api/v1/projects/{w.ProjectKey}/wiki/pages/{saved}", await PageDtoAsync(wikis, w, saved, baseUrl));
        }
        catch (WikiConflictException ex) { throw Conflict(ex); }
    }

    private static async Task<WikiPageDto> UpdatePage(HttpContext http, WikiService wikis, PublicBaseUrl url, string key, string path)
    {
        var w = await RequireWikiAsync(wikis, key);
        var page = RequirePath(path);
        var body = await ApiResults.ReadBodyAsync<UpdateWikiPageRequest>(http.Request);
        var current = await wikis.GetPageAsync(w, page) ?? throw ApiException.NotFound($"Wiki page '{page}' not found.");
        try
        {
            var saved = await wikis.SavePageAsync(w, page, body.NewPath ?? page, body.Content ?? current.Content, body.Message, body.Sha ?? current.BlobSha);
            return await PageDtoAsync(wikis, w, saved, url.For(http.Request));
        }
        catch (WikiConflictException ex) { throw Conflict(ex); }
    }

    private static async Task<IResult> DeletePage(WikiService wikis, string key, string path, string? sha)
    {
        var w = await RequireWikiAsync(wikis, key);
        var page = RequirePath(path);
        if (await wikis.GetPageAsync(w, page) == null) throw ApiException.NotFound($"Wiki page '{page}' not found.");
        try { await wikis.DeletePageAsync(w, page, sha); }
        catch (WikiConflictException ex) { throw Conflict(ex); }
        return TypedResults.NoContent();
    }

    private static async Task<List<CommitDto>> PageHistory(HttpContext http, WikiService wikis, string key, string? path)
    {
        var paging = Paging.From(http.Request);
        var w = await RequireWikiAsync(wikis, key);
        var (items, _) = await wikis.HistoryAsync(w, RequirePath(path), paging.Skip, paging.PerPage);
        return items.Select(Dto.Commit).ToList();
    }

    // ---------- Snippets ----------

    private static SnippetSummaryDto Summary(SnippetSummary s, string baseUrl) => new(s.Snippet.Id, s.Snippet.Title, s.Snippet.Visibility,
        Dto.User(s.Snippet.Author), s.Snippet.Project?.Key, s.FileNames, Dto.Utc(s.Snippet.CreatedAt), Dto.Utc(s.Snippet.UpdatedAt), baseUrl + s.Url);

    private static SnippetDto Full(SnippetView v, string baseUrl)
    {
        var s = v.Snippet;
        return new SnippetDto(s.Id, s.Title, s.Description, s.Visibility, Dto.User(s.Author), s.Project?.Key, v.Revision.Number, s.CurrentRevision,
            v.Revision.Files.Select(f => new SnippetFileDto(f.FileName, f.Content,
                $"{baseUrl}/api/v1/snippets/{s.Id}/files/{Uri.EscapeDataString(f.FileName)}/raw" + (v.IsCurrent ? "" : $"?rev={v.Revision.Number}"))).ToList(),
            Dto.Utc(s.CreatedAt), Dto.Utc(s.UpdatedAt), baseUrl + SnippetService.Url(s.Id), baseUrl + SnippetService.EmbedUrl(s.Id));
    }

    private static async Task<SnippetView> RequireSnippetAsync(SnippetService snippets, int id, int? rev = null) =>
        await snippets.GetAsync(id, rev) ?? throw ApiException.NotFound($"Snippet {id} not found.");

    private static SnippetInput Input(SaveSnippetRequest body, SnippetVisibility fallback) => new(body.Title ?? "", body.Description,
        body.Visibility ?? fallback, (body.Files ?? []).Select(f => new SnippetFileInput(f.FileName ?? "", f.Content ?? "")).ToList());

    private static async Task<List<SnippetSummaryDto>> ListSnippets(HttpContext http, SnippetService snippets, PublicBaseUrl url, string? scope)
    {
        var paging = Paging.From(http.Request);
        var list = scope?.ToLowerInvariant() switch
        {
            null or "" or "mine" => await snippets.ListMineAsync(),
            "explore" => await snippets.ExploreAsync(),
            _ => throw ApiException.BadRequest("scope must be mine or explore."),
        };
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, list, s => Summary(s, baseUrl));
    }

    private static async Task<List<SnippetSummaryDto>> ListProjectSnippets(HttpContext http, SnippetService snippets, IssueService issues, PublicBaseUrl url, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await issues.GetProjectAsync(key) ?? throw ApiException.NotFound($"Project '{key.ToUpperInvariant()}' not found.");
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, await snippets.ListForProjectAsync(project.Id), s => Summary(s, baseUrl));
    }

    private static async Task<SnippetDto> GetSnippet(HttpContext http, SnippetService snippets, PublicBaseUrl url, int id, int? rev) =>
        Full(await RequireSnippetAsync(snippets, id, rev), url.For(http.Request));

    private static async Task<IResult> CreateSnippet(HttpContext http, SnippetService snippets, IssueService issues, PublicBaseUrl url)
    {
        var body = await ApiResults.ReadBodyAsync<SaveSnippetRequest>(http.Request);
        int? projectId = null;
        if (!string.IsNullOrWhiteSpace(body.Project))
            projectId = (await issues.GetProjectAsync(body.Project) ?? throw ApiException.Unprocessable($"Project '{body.Project}' not found.")).Id;
        var created = await snippets.CreateAsync(Input(body, projectId != null ? SnippetVisibility.Project : SnippetVisibility.Private), projectId);
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/snippets/{created.Id}", Full(await RequireSnippetAsync(snippets, created.Id), baseUrl));
    }

    private static async Task<SnippetDto> UpdateSnippet(HttpContext http, SnippetService snippets, PublicBaseUrl url, int id)
    {
        var current = await RequireSnippetAsync(snippets, id);
        var body = await ApiResults.ReadBodyAsync<SaveSnippetRequest>(http.Request);
        // Omitted fields keep their current values.
        body = body with
        {
            Title = body.Title ?? current.Snippet.Title,
            Description = body.Description ?? current.Snippet.Description,
            Files = body.Files ?? current.Revision.Files.Select(f => new SnippetFileRequest(f.FileName, f.Content)).ToList(),
        };
        await snippets.UpdateAsync(id, Input(body, current.Snippet.Visibility));
        return Full(await RequireSnippetAsync(snippets, id), url.For(http.Request));
    }

    private static async Task<IResult> DeleteSnippet(SnippetService snippets, int id)
    {
        await RequireSnippetAsync(snippets, id);
        await snippets.DeleteAsync(id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RawFile(HttpContext http, SnippetService snippets, int id, string fileName, int? rev)
    {
        var file = await snippets.GetFileAsync(id, fileName, rev) ?? throw ApiException.NotFound($"File '{fileName}' not found in snippet {id}.");
        return WikiWebEndpoints.Raw(http, file, download: false);
    }

    private static async Task<List<SnippetCommentDto>> ListComments(SnippetService snippets, int id) =>
        (await RequireSnippetAsync(snippets, id)).Comments.Select(c => new SnippetCommentDto(c.Id, Dto.User(c.Author), c.Body, Dto.Utc(c.CreatedAt))).ToList();

    private static async Task<IResult> AddComment(HttpContext http, SnippetService snippets, ApiCaller caller, PublicBaseUrl url, int id)
    {
        await RequireSnippetAsync(snippets, id);
        var body = await ApiResults.ReadBodyAsync<CreateSnippetCommentRequest>(http.Request);
        var c = await snippets.AddCommentAsync(id, body.Body ?? "");
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/snippets/{id}/comments",
            new SnippetCommentDto(c.Id, Dto.User(caller.User), c.Body, Dto.Utc(c.CreatedAt)));
    }

    private static async Task<IResult> DeleteComment(SnippetService snippets, int id, int commentId)
    {
        await RequireSnippetAsync(snippets, id);
        await snippets.DeleteCommentAsync(id, commentId);
        return TypedResults.NoContent();
    }
}
