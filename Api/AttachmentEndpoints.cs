using GitBench.Data;
using GitBench.Services;
using GitBench.Services.Attachments;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace GitBench.Api;

public record AttachmentDto(int Id, string FileName, string ContentType, long Size, string Sha256, UserDto Uploader,
    DateTimeOffset CreatedAt, string Url, string ApiUrl, string Markdown);

/// <summary>/api/v1/projects/{key}/issues/{number}/attachments: list, upload (multipart), download and delete.</summary>
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/issues/{number:int}/attachments").WithTags("Attachments");
        g.MapGet("", List).WithName("ListIssueAttachments").WithSummary("List an issue's attachments");
        g.MapPost("", Upload).WithName("UploadIssueAttachment").WithSummary("Upload an attachment")
            .WithDescription("multipart/form-data with one file part (any field name). Needs EditIssues. Limits: Attachments:MaxBytes per file (default 25 MB) and Attachments:MaxPerIssue.")
            .Produces<AttachmentDto>(StatusCodes.Status201Created);
        g.MapGet("/{id:int}", Download).WithName("DownloadIssueAttachment").WithSummary("Download an attachment's content")
            .WithDescription("png/jpeg/gif/webp are served inline; everything else as application/octet-stream with Content-Disposition: attachment.");
        g.MapDelete("/{id:int}", Delete).WithName("DeleteIssueAttachment").WithSummary("Delete an attachment")
            .WithDescription("Allowed for the uploader or a Maintainer+.").Produces(StatusCodes.Status204NoContent);
    }

    private static AttachmentDto ToDto(Attachment a, string projectKey, int number, string baseUrl) => new(
        a.Id, a.FileName, a.ContentType, a.Size, a.Sha256, Dto.User(a.Uploader), Dto.Utc(a.CreatedAt),
        baseUrl + AttachmentFormat.Url(a.Id, a.FileName),
        $"{baseUrl}/api/v1/projects/{projectKey}/issues/{number}/attachments/{a.Id}",
        AttachmentFormat.MarkdownLink(a.Id, a.FileName, a.ContentType));

    private static async Task<Issue> RequireIssueAsync(IssueService issues, string key, int number) =>
        await issues.GetIssueAsync(key, number) ?? throw ApiException.NotFound($"Issue {key.ToUpperInvariant()}-{number} not found.");

    private static async Task<Attachment> RequireAttachmentAsync(AttachmentService attachments, Issue issue, int id) =>
        await attachments.FindAsync(id) is { } a && a.IssueId == issue.Id ? a : throw ApiException.NotFound($"Attachment {id} not found on {issue.Key}.");

    private static async Task<List<AttachmentDto>> List(HttpContext http, IssueService issues, AttachmentService attachments, PublicBaseUrl url, string key, int number)
    {
        var paging = Paging.From(http.Request);
        var issue = await RequireIssueAsync(issues, key, number);
        var baseUrl = url.For(http.Request);
        return paging.Apply(http, await attachments.ListAsync(issue.Id), a => ToDto(a, issue.Project.Key, number, baseUrl));
    }

    private static async Task<IResult> Upload(HttpContext http, IssueService issues, AttachmentService attachments, ProjectAccess access,
        ApiCaller caller, PublicBaseUrl url, string key, int number)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        await access.RequireAsync(issue.ProjectId, Permission.EditIssues);
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = attachments.MaxBytes + 1024 * 1024; // room for multipart framing

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type", "Send the file as multipart/form-data.");

        // Stream the first file part straight to disk (no buffering of the whole body).
        var reader = new MultipartReader(boundary, http.Request.Body);
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(http.RequestAborted)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || !cd.IsFileDisposition()) continue;
                var fileName = cd.FileNameStar.HasValue ? cd.FileNameStar.Value : HeaderUtilities.RemoveQuotes(cd.FileName).Value;
                var a = await attachments.UploadAsync(issue.Id, fileName, section.Body, actorId: caller.Id, ct: http.RequestAborted);
                var dto = ToDto(a, issue.Project.Key, number, url.For(http.Request));
                return TypedResults.Created(dto.ApiUrl, dto);
            }
        }
        catch (AttachmentTooLargeException ex)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", ex.Message);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", $"Files can be at most {AttachmentFormat.Size(attachments.MaxBytes)}.");
        }
        catch (InvalidDataException ex)
        {
            throw ApiException.BadRequest("Malformed multipart body: " + ex.Message);
        }
        throw ApiException.BadRequest("The request has no file part.");
    }

    private static async Task<IResult> Download(HttpContext http, IssueService issues, AttachmentService attachments, string key, int number, int id)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        var a = await RequireAttachmentAsync(attachments, issue, id);
        http.Response.Headers.CacheControl = "private, no-cache";
        return AttachmentDownloads.Serve(http, a, attachments.PathFor(a));
    }

    private static async Task<IResult> Delete(IssueService issues, AttachmentService attachments, string key, int number, int id)
    {
        var issue = await RequireIssueAsync(issues, key, number);
        var a = await RequireAttachmentAsync(attachments, issue, id);
        await attachments.DeleteAsync(a.Id);
        return TypedResults.NoContent();
    }
}
