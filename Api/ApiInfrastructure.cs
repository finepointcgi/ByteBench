using System.Text.Json;
using GitBench.Data;
using GitBench.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace GitBench.Api;

/// <summary>An API error that becomes a ProblemDetails response.</summary>
public sealed class ApiException(int status, string title, string? detail = null, IDictionary<string, object?>? extensions = null) : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IDictionary<string, object?>? Extensions { get; } = extensions;

    public static ApiException NotFound(string what) => new(StatusCodes.Status404NotFound, "Not Found", what);
    public static ApiException BadRequest(string detail) => new(StatusCodes.Status400BadRequest, "Bad Request", detail);
    public static ApiException Unprocessable(string detail) => new(StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity", detail);
}

/// <summary>Who is calling the API in this request (scoped). Set by <see cref="ApiFilter"/>.</summary>
public sealed class ApiCaller
{
    public User User { get; set; } = null!;
    public int Id => User.Id;
    /// <summary>The token's scopes, or null for a cookie session (which has every API scope).</summary>
    public TokenScopes? Scopes { get; set; }
    public PersonalAccessToken? Token { get; set; }
}

/// <summary>
/// Runs for every /api/v1 endpoint: authenticates (Bearer personal access token, else the cookie session), checks the
/// token scope for the HTTP method (GET/HEAD need read_api or write_api, everything else write_api), makes the caller
/// the acting user for <see cref="ProjectAccess"/>, and turns exceptions into ProblemDetails.
/// </summary>
public sealed class ApiFilter(AccessTokenService tokens, UserManager<User> users, ProjectAccess access, ApiCaller caller, ILogger<ApiFilter> log)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        ApiResults.PrepareResponse(http);

        var authError = await AuthenticateAsync(http);
        if (authError != null) return authError;

        var reading = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        if (caller.Scopes is { } scopes)
        {
            var ok = reading ? (scopes & (TokenScopes.ReadApi | TokenScopes.WriteApi)) != 0 : scopes.HasFlag(TokenScopes.WriteApi);
            if (!ok)
                return ApiResults.Problem(StatusCodes.Status403Forbidden, "Insufficient scope",
                    reading ? "This token needs the read_api or write_api scope." : "This token needs the write_api scope.");
        }
        else if (!reading && !ApiResults.IsJson(http.Request) && !http.Request.Headers.ContainsKey("X-Requested-With"))
        {
            // CSRF: a cross-site HTML form can neither send application/json nor set custom headers.
            return ApiResults.Problem(StatusCodes.Status403Forbidden, "Forbidden",
                "Cookie-authenticated writes must send a JSON body (Content-Type: application/json) or the X-Requested-With header.");
        }

        access.ActAs(caller.Id);
        try
        {
            return await next(context);
        }
        catch (ApiException ex)
        {
            return ApiResults.Problem(ex.Status, ex.Title, ex.Detail, ex.Extensions);
        }
        catch (ForbiddenException ex)
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, "Forbidden", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ApiResults.Problem(StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity", ex.Message);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "API error on {Method} {Path}", http.Request.Method, http.Request.Path);
            return ApiResults.Problem(StatusCodes.Status500InternalServerError, "Internal Server Error", "An unexpected error occurred.");
        }
    }

    /// <summary>Null on success (caller is set), else the 401 result.</summary>
    private async Task<IResult?> AuthenticateAsync(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.Length > 0)
        {
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Unauthorized(http, "Use 'Authorization: Bearer <personal access token>'.");
            var presented = header[7..].Trim();
            var token = await tokens.FindActiveAsync(presented);
            if (token == null || await users.IsLockedOutAsync(token.User))
            {
                log.LogInformation("API auth failed: invalid, expired or revoked token from {Ip}", http.Connection.RemoteIpAddress);
                return Unauthorized(http, "The access token is invalid, expired or revoked.", invalidToken: true);
            }
            caller.User = token.User;
            caller.Scopes = token.Scopes;
            caller.Token = token;
            return null;
        }

        if (http.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(http.User) is { } user && !await users.IsLockedOutAsync(user))
        {
            caller.User = user;
            caller.Scopes = null;
            return null;
        }
        return Unauthorized(http, "Authentication required. Send 'Authorization: Bearer <personal access token>'.");
    }

    private static IResult Unauthorized(HttpContext http, string detail, bool invalidToken = false)
    {
        http.Response.Headers.WWWAuthenticate = invalidToken ? "Bearer realm=\"GitBench\", error=\"invalid_token\"" : "Bearer realm=\"GitBench\"";
        return ApiResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", detail);
    }
}

public static class ApiResults
{
    /// <summary>JSON-only responses: no HTML status-code pages, no caching of private data.</summary>
    public static void PrepareResponse(HttpContext http)
    {
        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
    }

    /// <summary>A JSON error message without .NET type names.</summary>
    public static string InvalidJson(JsonException ex) =>
        ex.Path is { Length: > 1 } path ? $"Invalid JSON: unexpected value at {path}." : $"Invalid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}).";

    public static bool IsJson(HttpRequest request) =>
        request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false;

    public static IResult Problem(int status, string title, string? detail, IDictionary<string, object?>? extensions = null) =>
        TypedResults.Problem(detail: detail, statusCode: status, title: title, extensions: extensions);

    /// <summary>Reads and validates a JSON body (bad JSON is a 400 ProblemDetails, not an HTML error page).</summary>
    public static async Task<T> ReadBodyAsync<T>(HttpRequest request) where T : class
    {
        if (!IsJson(request))
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type", "Send the request body as application/json.");
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, Dto.Json, request.HttpContext.RequestAborted)
                   ?? throw ApiException.BadRequest("The request body is empty.");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest(InvalidJson(ex));
        }
    }
}

/// <summary><c>?page=&amp;perPage=</c> pagination with <c>X-Total-Count</c> and RFC 8288 <c>Link</c> headers.</summary>
public readonly record struct Paging(int Page, int PerPage)
{
    public const int DefaultPerPage = 30, MaxPerPage = 100;

    public int Skip => (Page - 1) * PerPage;

    public static Paging From(HttpRequest request)
    {
        int page = 1, perPage = DefaultPerPage;
        if (request.Query["page"] is { Count: > 0 } p && (!int.TryParse(p, out page) || page < 1))
            throw ApiException.BadRequest("page must be a positive integer.");
        if (request.Query["perPage"] is { Count: > 0 } pp && (!int.TryParse(pp, out perPage) || perPage < 1 || perPage > MaxPerPage))
            throw ApiException.BadRequest($"perPage must be between 1 and {MaxPerPage}.");
        return new Paging(page, perPage);
    }

    /// <summary>Pages an in-memory list and writes the headers.</summary>
    public List<TOut> Apply<TIn, TOut>(HttpContext http, IReadOnlyCollection<TIn> all, Func<TIn, TOut> map)
    {
        WriteHeaders(http, all.Count);
        return all.Skip(Skip).Take(PerPage).Select(map).ToList();
    }

    public void WriteHeaders(HttpContext http, int total)
    {
        http.Response.Headers["X-Total-Count"] = total.ToString();
        var last = Math.Max(1, (total + PerPage - 1) / PerPage);
        var links = new List<string>();
        var perPage = PerPage;
        string Url(int page)
        {
            var query = http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
            query["page"] = page.ToString();
            query["perPage"] = perPage.ToString();
            var baseUrl = http.RequestServices.GetRequiredService<PublicBaseUrl>().For(http.Request);
            return QueryHelpers.AddQueryString(baseUrl + http.Request.Path, query!);
        }
        if (Page < last) links.Add($"<{Url(Page + 1)}>; rel=\"next\"");
        if (Page > 1) links.Add($"<{Url(Math.Min(Page - 1, last))}>; rel=\"prev\"");
        links.Add($"<{Url(1)}>; rel=\"first\"");
        links.Add($"<{Url(last)}>; rel=\"last\"");
        http.Response.Headers.Link = string.Join(", ", links);
    }
}
