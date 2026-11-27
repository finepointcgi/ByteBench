using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Services;

namespace ByteBench.Api;

/// <summary>
/// Shared plumbing for the management endpoints (projects, members, repositories, sprints, boards, webhooks, …): strict request
/// bodies and the mapping of the services' <see cref="InvalidOperationException"/> messages to 404 / 409 / 422 problem responses.
/// The services stay the single source of truth for rules, permissions and audit entries; these endpoints only translate.
/// </summary>
public static partial class Management
{
    private static readonly JsonSerializerOptions StrictOptions = new(Dto.Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    [GeneratedRegex(@"JSON property '(?<name>[^']{1,80})'")]
    private static partial Regex UnmappedRx();

    /// <summary>Reads a JSON body; an unknown property is a 400 (so a typo can't silently do nothing).</summary>
    public static async Task<T> ReadAsync<T>(HttpRequest request) where T : class
    {
        if (!ApiResults.IsJson(request))
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type", "Send the request body as application/json.");
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, StrictOptions, request.HttpContext.RequestAborted)
                   ?? throw ApiException.BadRequest("The request body is empty.");
        }
        catch (JsonException ex) when (UnmappedRx().Match(ex.Message) is { Success: true } m)
        {
            throw ApiException.BadRequest($"Unknown property '{m.Groups["name"].Value}'.");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest(ApiResults.InvalidJson(ex));
        }
    }

    /// <summary>A required text field: trimmed, 422 when missing.</summary>
    public static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw ApiException.Unprocessable($"{name} is required.") : value.Trim();

    /// <summary>An enum value from its name (case-insensitive); 422 when unknown.</summary>
    public static T Enum<T>(string? value, string name, T? fallback = null) where T : struct, System.Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback ?? throw ApiException.Unprocessable($"{name} is required.");
        return System.Enum.TryParse<T>(value.Trim(), true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : throw ApiException.Unprocessable($"{name} must be one of: {string.Join(", ", System.Enum.GetNames<T>().Select(n => JsonNamingPolicy.CamelCase.ConvertName(n)))}.");
    }

    public static DateTime? Date(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    /// <summary>
    /// Runs a service call and maps its refusals: a missing thing is 404, a duplicate or state clash is 409, everything else the
    /// service rejects stays 422. Permission failures (<see cref="ForbiddenException"/>) pass through to the 403 handling.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> op)
    {
        try { return await op(); }
        catch (ForbiddenException) { throw; }
        catch (InvalidOperationException ex) { throw Map(ex); }
    }

    public static async Task RunAsync(Func<Task> op) => await RunAsync(async () => { await op(); return 0; });

    [GeneratedRegex(@"\balready\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyRx();

    private static ApiException Map(InvalidOperationException ex)
    {
        var m = ex.Message;
        if (m.EndsWith("not found.", StringComparison.OrdinalIgnoreCase) || m.Contains("is not a member", StringComparison.OrdinalIgnoreCase) || m.Contains("is not a direct member", StringComparison.OrdinalIgnoreCase))
            return ApiException.NotFound(m);
        if (m.StartsWith("Too many", StringComparison.Ordinal))
            return new ApiException(StatusCodes.Status429TooManyRequests, "Too Many Requests", m);
        if (AlreadyRx().IsMatch(m))
            return new ApiException(StatusCodes.Status409Conflict, "Conflict", m);
        return ApiException.Unprocessable(m);
    }
}
