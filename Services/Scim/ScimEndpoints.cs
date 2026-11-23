using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Scim;

/// <summary>
/// The SCIM 2.0 server (RFC 7643/7644) at <c>/scim/v2</c>: ServiceProviderConfig, Schemas, ResourceTypes, Users (list with
/// filter and paging, get, create, replace, patch, DELETE = deactivate) and Groups (see <see cref="ScimGroupService"/>). Responses are
/// <c>application/scim+json</c>; errors are SCIM error bodies.
/// <para>
/// Every request goes through <see cref="ScimGate"/>: SCIM must be switched on with a token (else 403), the client IP must
/// not have too many recent token failures (else 429), the bearer token must be valid and unrevoked (else 401), and the
/// body must fit <c>Scim:MaxBodyKilobytes</c> (else 413).
/// </para>
/// </summary>
public static class ScimEndpoints
{
    public const string ContentType = "application/scim+json";
    public const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";

    public static IServiceCollection AddScim(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScimOptions>(configuration.GetSection(ScimOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ScimAuthLimiter>();
        services.AddScoped<ScimTokenService>();
        services.AddScoped<ScimUserService>();
        services.AddScoped<ScimGroupService>();
        return services;
    }

    public static IEndpointRouteBuilder MapScim(this IEndpointRouteBuilder app)
    {
        var scim = app.MapGroup("/scim/v2").AllowAnonymous().DisableAntiforgery().ExcludeFromDescription().AddEndpointFilter<ScimGate>();

        scim.MapGet("/ServiceProviderConfig", (HttpContext http, IOptionsMonitor<ScimOptions> o) => Json(new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
            ["patch"] = new JsonObject { ["supported"] = true },
            ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
            ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = o.CurrentValue.MaxPageSize },
            ["changePassword"] = new JsonObject { ["supported"] = false },
            ["sort"] = new JsonObject { ["supported"] = false },
            ["etag"] = new JsonObject { ["supported"] = false },
            ["authenticationSchemes"] = new JsonArray(new JsonObject
            {
                ["type"] = "oauthbearertoken", ["name"] = "Bearer token", ["description"] = "A SCIM token from /admin/authentication (Authorization: Bearer bbscim_…).", ["primary"] = true,
            }),
            ["meta"] = new JsonObject { ["resourceType"] = "ServiceProviderConfig", ["location"] = BaseUrl(http) + "/ServiceProviderConfig" },
        }));

        scim.MapGet("/ResourceTypes", (HttpContext http) => Json(List([UserResourceType(http), GroupResourceType(http)], 2, 1)));
        scim.MapGet("/ResourceTypes/{name}", (HttpContext http, string name) => name switch
        {
            "User" => Json(UserResourceType(http)),
            "Group" => Json(GroupResourceType(http)),
            _ => Error(404, $"Resource type {name} not found."),
        });
        scim.MapGet("/Schemas", () => Json(List([UserSchemaDoc(), GroupSchemaDoc()], 2, 1)));
        scim.MapGet("/Schemas/{id}", (string id) => id == ScimUserService.UserSchema ? Json(UserSchemaDoc())
            : id == ScimUserService.GroupSchema ? Json(GroupSchemaDoc()) : Error(404, $"Schema {id} not found."));

        // ----- Users
        scim.MapGet("/Users", async (HttpContext http, ScimUserService users, IOptionsMonitor<ScimOptions> o, string? filter, int? startIndex, int? count) =>
        {
            var (start, size) = Paging(o.CurrentValue, startIndex, count);
            var (page, total) = await users.ListAsync(filter, start, size);
            return Json(List(page.Select(u => (JsonNode)ScimUserService.ToJson(u, BaseUrl(http))).ToList(), total, start));
        });
        scim.MapGet("/Users/{id}", async (HttpContext http, ScimUserService users, string id) =>
            int.TryParse(id, out var uid) && await users.GetAsync(uid) is { } u ? Json(ScimUserService.ToJson(u, BaseUrl(http))) : Error(404, $"User {id} not found."));
        scim.MapPost("/Users", async (HttpContext http, ScimUserService users) =>
        {
            var (u, _) = await users.CreateAsync(Body(http));
            var json = ScimUserService.ToJson(u, BaseUrl(http));
            http.Response.Headers.Location = json["meta"]!["location"]!.GetValue<string>();
            return Json(json, 201);
        });
        scim.MapPut("/Users/{id}", async (HttpContext http, ScimUserService users, string id) =>
            Json(ScimUserService.ToJson(await users.ReplaceAsync(Id(id, "User"), Body(http)), BaseUrl(http))));
        scim.MapPatch("/Users/{id}", async (HttpContext http, ScimUserService users, string id) =>
            Json(ScimUserService.ToJson(await users.PatchAsync(Id(id, "User"), Body(http)), BaseUrl(http))));
        scim.MapDelete("/Users/{id}", async (ScimUserService users, string id) =>
        {
            await users.DeactivateAsync(Id(id, "User"));
            return Results.NoContent();
        });

        // ----- Groups (writes only on groups SCIM created or adopted: see ScimGroupService)
        scim.MapGet("/Groups", async (HttpContext http, ScimGroupService groups, IOptionsMonitor<ScimOptions> o, string? filter, int? startIndex, int? count) =>
        {
            var (start, size) = Paging(o.CurrentValue, startIndex, count);
            var (page, total) = await groups.ListAsync(filter, start, size, BaseUrl(http));
            return Json(List(page.Cast<JsonNode>().ToList(), total, start));
        });
        scim.MapGet("/Groups/{id}", async (HttpContext http, ScimGroupService groups, string id) =>
            int.TryParse(id, out var gid) && await groups.GetAsync(gid, BaseUrl(http)) is { } g ? Json(g) : Error(404, $"Group {id} not found."));
        scim.MapPost("/Groups", async (HttpContext http, ScimGroupService groups) =>
        {
            var json = await groups.CreateAsync(Body(http), BaseUrl(http));
            http.Response.Headers.Location = json["meta"]!["location"]!.GetValue<string>();
            return Json(json, 201);
        });
        scim.MapPut("/Groups/{id}", async (HttpContext http, ScimGroupService groups, string id) =>
            Json(await groups.ReplaceAsync(Id(id, "Group"), Body(http), BaseUrl(http))));
        scim.MapPatch("/Groups/{id}", async (HttpContext http, ScimGroupService groups, string id) =>
            Json(await groups.PatchAsync(Id(id, "Group"), Body(http), BaseUrl(http))));
        scim.MapDelete("/Groups/{id}", async (ScimGroupService groups, string id) =>
        {
            await groups.DeleteAsync(Id(id, "Group"));
            return Results.NoContent();
        });
        scim.MapPost("/Bulk", () => Error(501, "Bulk operations are not supported."));

        // Unknown SCIM paths: a SCIM 404, not the site's HTML page.
        scim.MapMethods("/{**rest}", ["GET", "POST", "PUT", "PATCH", "DELETE"], (string rest) => Error(404, $"/scim/v2/{rest} doesn't exist."));
        return app;
    }

    // ------------------------------------------------------------------ helpers

    public static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/scim/v2";

    private static (int Start, int Count) Paging(ScimOptions o, int? startIndex, int? count)
    {
        var max = Math.Max(1, o.MaxPageSize);
        return (Math.Max(1, startIndex ?? 1), Math.Clamp(count ?? Math.Min(o.DefaultPageSize, max), 0, max));
    }

    private static JsonObject List(List<JsonNode> resources, int total, int start) => new()
    {
        ["schemas"] = new JsonArray(ScimUserService.ListSchema),
        ["totalResults"] = total,
        ["startIndex"] = start,
        ["itemsPerPage"] = resources.Count,
        ["Resources"] = new JsonArray(resources.Select(r => r.DeepClone()).ToArray()),
    };

    private static int Id(string id, string what) => int.TryParse(id, out var v) ? v : throw new ScimError(404, $"{what} {id} not found.");

    public static IResult Json(JsonNode body, int status = 200) => Results.Text(body.ToJsonString(), ContentType, Encoding.UTF8, status);

    public static IResult Error(int status, string detail, string? scimType = null)
    {
        var body = new JsonObject { ["schemas"] = new JsonArray(ErrorSchema), ["status"] = status.ToString(), ["detail"] = detail };
        if (scimType != null) body["scimType"] = scimType;
        return Json(body, status);
    }

    /// <summary>The JSON request body (already size-checked by <see cref="ScimGate"/>).</summary>
    private static JsonObject Body(HttpContext http)
    {
        var bytes = http.Items[ScimGate.BodyKey] as byte[] ?? [];
        if (bytes.Length == 0) throw new ScimError(400, "The request has no body.", "invalidSyntax");
        try
        {
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                   ?? throw new ScimError(400, "The body must be a JSON object.", "invalidSyntax");
        }
        catch (JsonException) { throw new ScimError(400, "The body isn't valid JSON.", "invalidSyntax"); }
    }

    private static JsonObject UserResourceType(HttpContext http) => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ResourceType"),
        ["id"] = "User", ["name"] = "User", ["endpoint"] = "/Users", ["description"] = "ByteBench accounts", ["schema"] = ScimUserService.UserSchema,
        ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = BaseUrl(http) + "/ResourceTypes/User" },
    };

    private static JsonObject GroupResourceType(HttpContext http) => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ResourceType"),
        ["id"] = "Group", ["name"] = "Group", ["endpoint"] = "/Groups", ["description"] = "ByteBench groups (writable when created by SCIM or mapped with Scim:GroupMappings)", ["schema"] = ScimUserService.GroupSchema,
        ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = BaseUrl(http) + "/ResourceTypes/Group" },
    };

    private static JsonObject Attribute(string name, string type, bool required = false, string mutability = "readWrite", string uniqueness = "none",
        bool multiValued = false, JsonArray? sub = null)
    {
        var a = new JsonObject
        {
            ["name"] = name, ["type"] = type, ["multiValued"] = multiValued, ["required"] = required, ["caseExact"] = false,
            ["mutability"] = mutability, ["returned"] = "default", ["uniqueness"] = uniqueness,
        };
        if (sub != null) a["subAttributes"] = sub;
        return a;
    }

    private static JsonObject UserSchemaDoc() => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Schema"),
        ["id"] = ScimUserService.UserSchema, ["name"] = "User", ["description"] = "ByteBench account",
        ["attributes"] = new JsonArray(
            Attribute("userName", "string", required: true, uniqueness: "server"),
            Attribute("externalId", "string"),
            Attribute("displayName", "string"),
            Attribute("name", "complex", sub: new JsonArray(Attribute("formatted", "string"), Attribute("givenName", "string"), Attribute("familyName", "string"))),
            Attribute("emails", "complex", multiValued: true, sub: new JsonArray(Attribute("value", "string"), Attribute("type", "string"), Attribute("primary", "boolean"))),
            Attribute("active", "boolean")),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema" },
    };

    private static JsonObject GroupSchemaDoc() => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Schema"),
        ["id"] = ScimUserService.GroupSchema, ["name"] = "Group", ["description"] = "ByteBench group",
        ["attributes"] = new JsonArray(
            Attribute("displayName", "string", required: true),
            Attribute("externalId", "string"),
            Attribute("members", "complex", multiValued: true, sub: new JsonArray(Attribute("value", "string", mutability: "immutable"), Attribute("display", "string", mutability: "readOnly")))),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema" },
    };
}

/// <summary>Switch, rate limit, bearer token and body size checks for every SCIM request; turns <see cref="ScimError"/> into SCIM error responses.</summary>
public sealed class ScimGate(ScimTokenService tokens, ScimAuthLimiter limiter, IOptionsMonitor<ScimOptions> options, ILogger<ScimGate> log) : IEndpointFilter
{
    public const string BodyKey = "scim.body";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!await tokens.IsActiveAsync(http.RequestAborted))
            return ScimEndpoints.Error(403, "SCIM provisioning is turned off on this server (an administrator turns it on and creates a token at /admin/authentication).");

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (limiter.IsBlocked(ip))
        {
            http.Response.Headers.RetryAfter = (Math.Max(1, options.CurrentValue.FailureWindowMinutes) * 60).ToString();
            return ScimEndpoints.Error(429, "Too many failed authentication attempts from this address. Try again later.");
        }
        var auth = http.Request.Headers.Authorization.ToString();
        var bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        var token = await tokens.AuthenticateAsync(bearer, http.RequestAborted);
        if (token == null)
        {
            limiter.RecordFailure(ip);
            log.LogWarning("SCIM request from {Ip} with a missing, unknown or revoked token", ip);
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"ByteBench SCIM\"";
            return ScimEndpoints.Error(401, "A valid SCIM bearer token is required.");
        }

        var max = Math.Clamp(options.CurrentValue.MaxBodyKilobytes, 1, 4096) * 1024;
        if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method) || HttpMethods.IsPatch(http.Request.Method))
        {
            if (http.Request.ContentLength > max) return ScimEndpoints.Error(413, $"The request body is larger than {max / 1024} KiB.");
            var type = http.Request.ContentType ?? "";
            if (type.Length > 0 && !type.StartsWith(ScimEndpoints.ContentType, StringComparison.OrdinalIgnoreCase) && !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return ScimEndpoints.Error(415, "Send application/scim+json.");
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > max) return ScimEndpoints.Error(413, $"The request body is larger than {max / 1024} KiB.");
            }
            http.Items[BodyKey] = ms.ToArray();
        }

        try { return await next(context); }
        catch (ScimError e) { return ScimEndpoints.Error(e.Status, e.Message, e.ScimType); }
        catch (InvalidOperationException e) { return ScimEndpoints.Error(400, e.Message); }
    }
}
