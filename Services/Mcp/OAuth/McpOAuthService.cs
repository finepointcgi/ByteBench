using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ByteBench.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Mcp.OAuth;

/// <summary>Configuration section <c>Mcp:OAuth</c>.</summary>
public sealed class McpOAuthOptions
{
    public const string Section = "Mcp:OAuth";
    /// <summary>Off: no OAuth endpoints or metadata, and <c>/mcp</c> accepts only personal access tokens.</summary>
    public bool Enabled { get; set; } = true;
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;
    public int CodeSeconds { get; set; } = 60;
    /// <summary>Dynamic client registrations accepted from one client IP per hour.</summary>
    public int MaxRegistrationsPerIpPerHour { get; set; } = 20;
    /// <summary>Registered clients in total; further registrations are refused.</summary>
    public int MaxClients { get; set; } = 1000;
}

/// <summary>An OAuth error response (RFC 6749 §5.2 / RFC 7591 §3.2.2).</summary>
public sealed class OAuthError(int status, string error, string description) : Exception(description)
{
    public int Status { get; } = status;
    public string Error { get; } = error;
    public string Description { get; } = description;
}

/// <summary>Who an OAuth access token at <c>/mcp</c> acts as.</summary>
public sealed record McpOAuthPrincipal(User User, TokenScopes Scopes, int GrantId, int TokenId);

/// <summary>A validated authorization request (the consent page shows it; approving it issues a code).</summary>
public sealed record AuthorizeRequest(OAuthClient Client, string RedirectUri, string State, string CodeChallenge, TokenScopes Scopes, string? Resource)
{
    public string ScopeText => McpOAuthService.ScopeString(Scopes);
}

/// <summary>An authorization request that can't go back to the client: shown as an error page, never redirected.</summary>
public sealed class AuthorizeFatal(string message) : Exception(message);

/// <summary>An authorization request error that goes back to the (verified) redirect URI as <c>?error=…</c>.</summary>
public sealed class AuthorizeRedirectError(string redirectUri, string? state, string error, string description) : Exception(description)
{
    public string RedirectUri { get; } = redirectUri;
    public string? State { get; } = state;
    public string Error { get; } = error;
}

/// <summary>A connected app on the profile page.</summary>
public sealed record ConnectedApp(int GrantId, string ClientName, string RedirectHost, TokenScopes Scopes, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>Counts dynamic client registrations per client IP in a sliding hour.</summary>
public sealed class OAuthRegistrationLimiter(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> hits = new();

    public bool TryAcquire(string ip, int perHour)
    {
        if (perHour <= 0) return true;
        var now = clock.GetUtcNow();
        var q = hits.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
            if (q.Count >= perHour) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// OAuth 2.1 for the MCP server: dynamic client registration of public clients, authorization codes with mandatory S256
/// PKCE, opaque access tokens (<c>bbo_…</c>, only accepted by <c>/mcp</c>) and rotating refresh tokens (<c>bbr_…</c>) with
/// reuse detection. Only SHA-256 hashes of codes and tokens are stored. Scopes map to the personal access token scopes:
/// <c>read</c> = read_api, <c>write</c> = write_api.
/// </summary>
public sealed partial class McpOAuthService(IDbContextFactory<AppDbContext> dbFactory, Audit.AuditLog audit, IOptionsMonitor<McpOAuthOptions> options,
    OAuthRegistrationLimiter limiter, TimeProvider clock, ILogger<McpOAuthService> log)
{
    public const string AccessPrefix = "bbo_", RefreshPrefix = "bbr_", ClientPrefix = "bbc_";
    public const int MaxRedirectUris = 10, MaxRedirectUriLength = 2000;

    private McpOAuthOptions O => options.CurrentValue;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string Random(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    // ------------------------------------------------------------------ scopes

    public static string ScopeString(TokenScopes s) =>
        s.HasFlag(TokenScopes.WriteApi) ? (s.HasFlag(TokenScopes.ReadApi) ? "read write" : "write") : "read";

    /// <summary>"read write" → scopes. Empty: read only. Unknown scope names: null.</summary>
    public static TokenScopes? ParseScopes(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return TokenScopes.ReadApi;
        var result = TokenScopes.None;
        foreach (var s in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            switch (s)
            {
                case "read": result |= TokenScopes.ReadApi; break;
                case "write": result |= TokenScopes.ReadApi | TokenScopes.WriteApi; break; // write includes reading
                default: return null;
            }
        return result;
    }

    // ------------------------------------------------------------------ registration (RFC 7591)

    [GeneratedRegex("^[a-z][a-z0-9+.-]{0,63}$")]
    private static partial Regex SchemeRx();

    private static readonly HashSet<string> ForbiddenSchemes = ["javascript", "data", "file", "vbscript", "about", "blob", "ftp", "ws", "wss", "mailto", "view-source", "chrome", "jar", "filesystem"];

    /// <summary>
    /// Whether <paramref name="uri"/> may be registered: <c>https://</c> (any host), <c>http://</c> only on a loopback host
    /// (<c>127.0.0.1</c>, <c>localhost</c>, <c>[::1]</c>, any port), or a private-use scheme for a native app. No fragments,
    /// no user info. Returns the reason it can't, or null.
    /// </summary>
    public static string? RedirectUriProblem(string uri)
    {
        if (uri.Length is 0 or > MaxRedirectUriLength) return "is empty or too long";
        if (uri.Any(char.IsControl) || uri.Contains(' ')) return "contains spaces or control characters";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return "is not an absolute URI";
        if (uri.Contains('#')) return "must not contain a fragment";
        var scheme = u.Scheme.ToLowerInvariant();
        if (!string.IsNullOrEmpty(u.UserInfo)) return "must not contain user information";
        if (scheme == "https") return string.IsNullOrEmpty(u.Host) ? "has no host" : null;
        if (scheme == "http")
            return u.Host is "127.0.0.1" or "localhost" or "[::1]" ? null : "uses http:// on a host that isn't loopback (127.0.0.1, localhost or [::1])";
        if (!SchemeRx().IsMatch(scheme) || ForbiddenSchemes.Contains(scheme)) return $"uses a scheme ({scheme}) that isn't allowed";
        return null;
    }

    /// <summary>What the consent page shows as where the code goes: the host, or the scheme of a native app.</summary>
    public static string RedirectDisplay(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
            ? u.Scheme is "http" or "https" ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") : u.Scheme + ":"
            : "?";

    /// <summary>Strips control characters and trims a client-supplied name.</summary>
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c) && c is not ('‮' or '‭' or '⁦' or '⁧' or '⁨' or '⁩')).ToArray()).Trim();
        if (s.Length == 0) s = "Unnamed MCP client";
        return s.Length > 100 ? s[..100] : s;
    }

    public async Task<OAuthClient> RegisterAsync(JsonObject body, string ip)
    {
        static OAuthError Bad(string error, string d) => new(400, error, d);
        if (body["redirect_uris"] is not JsonArray uris || uris.Count == 0) throw Bad("invalid_redirect_uri", "redirect_uris is required.");
        if (uris.Count > MaxRedirectUris) throw Bad("invalid_redirect_uri", $"At most {MaxRedirectUris} redirect URIs.");
        var list = new List<string>();
        foreach (var n in uris)
        {
            if (n is not JsonValue v || !v.TryGetValue<string>(out var uri)) throw Bad("invalid_redirect_uri", "Each redirect URI must be a string.");
            if (RedirectUriProblem(uri) is { } problem) throw Bad("invalid_redirect_uri", $"The redirect URI {problem}.");
            if (!list.Contains(uri, StringComparer.Ordinal)) list.Add(uri);
        }
        string? Str(string key) => body[key] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
        if (body["token_endpoint_auth_method"] != null && Str("token_endpoint_auth_method") != "none")
            throw Bad("invalid_client_metadata", "Only public clients are supported: token_endpoint_auth_method must be \"none\".");
        if (body["grant_types"] is JsonArray gt && gt.Any(g => g?.GetValue<string>() is not ("authorization_code" or "refresh_token")))
            throw Bad("invalid_client_metadata", "grant_types may contain only authorization_code and refresh_token.");
        if (body["response_types"] is JsonArray rt && rt.Any(r => r?.GetValue<string>() != "code"))
            throw Bad("invalid_client_metadata", "response_types may contain only code.");
        if (body["scope"] != null && ParseScopes(Str("scope")) == null) throw Bad("invalid_client_metadata", "scope may contain only read and write.");
        if (Str("client_name") is { Length: > 1000 }) throw Bad("invalid_client_metadata", "client_name is too long.");

        if (!limiter.TryAcquire(ip, O.MaxRegistrationsPerIpPerHour))
            throw new OAuthError(429, "too_many_requests", "Too many client registrations from this address. Try again later.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.OAuthClients.CountAsync() >= Math.Max(0, O.MaxClients))
            throw new OAuthError(403, "access_denied", "This server has reached its limit of registered OAuth clients. Ask an administrator.");

        var client = new OAuthClient
        {
            ClientId = ClientPrefix + Random(24), ClientName = CleanName(Str("client_name")), RedirectUris = string.Join('\n', list), CreatedAt = Now,
        };
        db.OAuthClients.Add(client);
        await db.SaveChangesAsync();
        await audit.RecordAsync("oauth.client_registered", "oauth_client", client.ClientId,
            details: new { clientName = client.ClientName, redirectHosts = list.Select(RedirectDisplay).Distinct().ToList() }, anonymous: true, actorName: "OAuth client");
        return client;
    }

    // ------------------------------------------------------------------ authorization

    /// <summary>Validates an authorization request (GET, and again on the consent POST).</summary>
    public async Task<AuthorizeRequest> ValidateAuthorizeAsync(Func<string, string?> p, string mcpResource)
    {
        var clientId = p("client_id");
        if (string.IsNullOrEmpty(clientId)) throw new AuthorizeFatal("The request has no client_id.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId)
                     ?? throw new AuthorizeFatal("The application isn't registered with this server (unknown client_id).");
        var redirect = p("redirect_uri");
        if (string.IsNullOrEmpty(redirect)) throw new AuthorizeFatal("The request has no redirect_uri.");
        // Exact string comparison with a registered URI: no prefix, case or port leniency.
        if (!client.RedirectUriList.Contains(redirect, StringComparer.Ordinal))
            throw new AuthorizeFatal("The redirect_uri isn't one the application registered, so ByteBench won't send you there.");
        var state = p("state");
        if (string.IsNullOrEmpty(state)) throw new AuthorizeFatal("The request has no state parameter. The application must send one.");
        if (state.Length > 1000) throw new AuthorizeFatal("The state parameter is too long.");

        AuthorizeRedirectError Err(string error, string d) => new(redirect, state, error, d);
        if (p("response_type") != "code") throw Err("unsupported_response_type", "Only response_type=code is supported.");
        var challenge = p("code_challenge");
        var method = p("code_challenge_method");
        if (string.IsNullOrEmpty(challenge)) throw Err("invalid_request", "PKCE is required: send code_challenge and code_challenge_method=S256.");
        if (method != "S256") throw Err("invalid_request", "Only the S256 code_challenge_method is supported (plain is refused).");
        if (challenge.Length != 43 || !challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw Err("invalid_request", "code_challenge must be the base64url SHA-256 of the verifier (43 characters).");
        var scopes = ParseScopes(p("scope")) ?? throw Err("invalid_scope", "Unknown scope: use read and/or write.");
        var resource = p("resource");
        if (!string.IsNullOrEmpty(resource) && !SameResource(resource, mcpResource))
            throw Err("invalid_target", "resource must be this server's MCP endpoint.");
        return new AuthorizeRequest(client, redirect, state, challenge, scopes, string.IsNullOrEmpty(resource) ? null : resource);
    }

    public static bool SameResource(string given, string mcpResource) =>
        string.Equals(given.TrimEnd('/'), mcpResource.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>The user approved: issues a single-use code bound to the client, redirect URI, PKCE challenge and user.</summary>
    public async Task<string> ApproveAsync(AuthorizeRequest r, int userId)
    {
        var code = Random();
        await using var db = await dbFactory.CreateDbContextAsync();
        await PruneAsync(db);
        db.OAuthCodes.Add(new OAuthCode
        {
            CodeHash = Hash(code), ClientId = r.Client.Id, UserId = userId, Scopes = r.Scopes, RedirectUri = r.RedirectUri,
            CodeChallenge = r.CodeChallenge, Resource = r.Resource, CreatedAt = Now, ExpiresAt = Now.AddSeconds(Math.Clamp(O.CodeSeconds, 5, 600)),
        });
        await db.SaveChangesAsync();
        await audit.RecordAsync("oauth.consent_granted", "oauth_client", r.Client.ClientId,
            details: new { clientName = r.Client.ClientName, redirectHost = RedirectDisplay(r.RedirectUri), scope = r.ScopeText }, actorId: userId);
        return code;
    }

    public Task DenyAsync(AuthorizeRequest r, int userId) =>
        audit.RecordAsync("oauth.consent_denied", "oauth_client", r.Client.ClientId,
            details: new { clientName = r.Client.ClientName, redirectHost = RedirectDisplay(r.RedirectUri), scope = r.ScopeText }, actorId: userId);

    /// <summary>Deletes codes and access tokens long past their expiry (refresh tokens stay until they expire, for reuse detection).</summary>
    private async Task PruneAsync(AppDbContext db)
    {
        var cutoff = Now.AddHours(-1);
        await db.OAuthCodes.Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync();
        await db.OAuthTokens.Where(t => t.ExpiresAt < cutoff).ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------------ token endpoint

    public sealed record TokenResponse(string AccessToken, int ExpiresIn, string RefreshToken, string Scope);

    private static OAuthError InvalidGrant(string d) => new(400, "invalid_grant", d);

    private static bool UserActive(User u, DateTime now) => u.DeactivatedAt == null && !(u.LockoutEnd > now);

    public async Task<TokenResponse> ExchangeCodeAsync(IFormCollection f, string mcpResource)
    {
        string? P(string k) => f[k].Count == 1 ? f[k].ToString() : f[k].Count == 0 ? null : throw new OAuthError(400, "invalid_request", $"{k} was sent more than once.");
        var code = P("code");
        var clientId = P("client_id");
        var redirect = P("redirect_uri");
        var verifier = P("code_verifier");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirect))
            throw new OAuthError(400, "invalid_request", "code, client_id and redirect_uri are required.");
        if (string.IsNullOrEmpty(verifier)) throw new OAuthError(400, "invalid_request", "code_verifier is required (PKCE).");
        if (verifier.Length is < 43 or > 128 || !verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            throw new OAuthError(400, "invalid_request", "code_verifier must be 43-128 unreserved characters.");
        var resource = P("resource");
        if (!string.IsNullOrEmpty(resource) && !SameResource(resource, mcpResource)) throw new OAuthError(400, "invalid_target", "resource must be this server's MCP endpoint.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId)
                     ?? throw new OAuthError(401, "invalid_client", "Unknown client_id.");
        var hash = Hash(code);
        var row = await db.OAuthCodes.FirstOrDefaultAsync(c => c.CodeHash == hash) ?? throw InvalidGrant("The authorization code is invalid.");
        if (row.UsedAt != null)
        {
            // A code presented twice: someone else has it. Kill what it was exchanged for.
            if (row.GrantId is { } leaked) await RevokeGrantCoreAsync(db, leaked, "code_reuse", null);
            throw InvalidGrant("The authorization code was already used.");
        }
        if (row.ExpiresAt <= Now) throw InvalidGrant("The authorization code has expired.");
        if (row.ClientId != client.Id) throw InvalidGrant("The authorization code was issued to another client.");
        if (!string.Equals(row.RedirectUri, redirect, StringComparison.Ordinal)) throw InvalidGrant("redirect_uri doesn't match the authorization request.");
        var computed = Encoding.ASCII.GetBytes(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        if (!CryptographicOperations.FixedTimeEquals(computed, Encoding.ASCII.GetBytes(row.CodeChallenge))) throw InvalidGrant("The code_verifier doesn't match the code_challenge.");
        if (row.Resource != null && !string.IsNullOrEmpty(resource) && !SameResource(resource, row.Resource)) throw new OAuthError(400, "invalid_target", "resource doesn't match the authorization request.");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == row.UserId);
        if (user == null || !UserActive(user, Now)) throw InvalidGrant("The account that approved this request can't sign in.");

        // Single use, also under concurrency: only one exchange can flip UsedAt.
        var now = Now;
        if (await db.OAuthCodes.Where(c => c.Id == row.Id && c.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now)) == 0)
            throw InvalidGrant("The authorization code was already used.");

        var grant = new OAuthGrant { ClientId = client.Id, UserId = user.Id, Scopes = row.Scopes, CreatedAt = now, LastUsedAt = now };
        db.OAuthGrants.Add(grant);
        await db.SaveChangesAsync();
        await db.OAuthCodes.Where(c => c.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.GrantId, grant.Id));
        var response = await IssuePairAsync(db, grant.Id, row.Scopes);
        await audit.RecordAsync("oauth.token_issued", "oauth_grant", grant.Id,
            details: new { clientId = client.ClientId, clientName = client.ClientName, scope = response.Scope }, actorId: user.Id);
        return response;
    }

    public async Task<TokenResponse> RefreshAsync(IFormCollection f, string mcpResource)
    {
        string? P(string k) => f[k].Count == 1 ? f[k].ToString() : f[k].Count == 0 ? null : throw new OAuthError(400, "invalid_request", $"{k} was sent more than once.");
        var presented = P("refresh_token");
        var clientId = P("client_id");
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(clientId)) throw new OAuthError(400, "invalid_request", "refresh_token and client_id are required.");
        var resource = P("resource");
        if (!string.IsNullOrEmpty(resource) && !SameResource(resource, mcpResource)) throw new OAuthError(400, "invalid_target", "resource must be this server's MCP endpoint.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId)
                     ?? throw new OAuthError(401, "invalid_client", "Unknown client_id.");
        if (!presented.StartsWith(RefreshPrefix, StringComparison.Ordinal)) throw InvalidGrant("The refresh token is invalid.");
        var hash = Hash(presented);
        var token = await db.OAuthTokens.Include(t => t.Grant).ThenInclude(g => g.User)
                        .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == OAuthTokenKind.Refresh)
                    ?? throw InvalidGrant("The refresh token is invalid.");
        var grant = token.Grant;
        if (grant.ClientId != client.Id) throw InvalidGrant("The refresh token was issued to another client.");
        if (token.RotatedAt != null)
        {
            await RevokeGrantCoreAsync(db, grant.Id, "refresh_reuse", null);
            await audit.RecordAsync("oauth.refresh_reuse_detected", "oauth_grant", grant.Id,
                details: new { clientId = client.ClientId, clientName = client.ClientName }, actorId: grant.UserId);
            throw InvalidGrant("The refresh token was already used. The whole grant has been revoked; sign in again.");
        }
        if (grant.RevokedAt != null || token.RevokedAt != null) throw InvalidGrant("The grant has been revoked.");
        if (token.ExpiresAt <= Now) throw InvalidGrant("The refresh token has expired.");
        if (!UserActive(grant.User, Now)) throw InvalidGrant("The account can't sign in.");

        var scopes = token.Scopes;
        if (P("scope") is { Length: > 0 } requested)
        {
            var asked = ParseScopes(requested) ?? throw new OAuthError(400, "invalid_scope", "Unknown scope: use read and/or write.");
            if ((asked & ~token.Scopes) != 0) throw new OAuthError(400, "invalid_scope", "A refresh can't add scopes that weren't granted.");
            scopes = asked;
        }

        var now = Now;
        if (await db.OAuthTokens.Where(t => t.Id == token.Id && t.RotatedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RotatedAt, now)) == 0)
        {
            await RevokeGrantCoreAsync(db, grant.Id, "refresh_reuse", null);
            await audit.RecordAsync("oauth.refresh_reuse_detected", "oauth_grant", grant.Id, details: new { clientId = client.ClientId }, actorId: grant.UserId);
            throw InvalidGrant("The refresh token was already used. The whole grant has been revoked; sign in again.");
        }
        await db.OAuthGrants.Where(g => g.Id == grant.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now));
        var response = await IssuePairAsync(db, grant.Id, scopes);
        await audit.RecordAsync("oauth.token_refreshed", "oauth_grant", grant.Id, details: new { clientId = client.ClientId, scope = response.Scope }, actorId: grant.UserId);
        return response;
    }

    private async Task<TokenResponse> IssuePairAsync(AppDbContext db, int grantId, TokenScopes scopes)
    {
        var access = AccessPrefix + Random();
        var refresh = RefreshPrefix + Random();
        var minutes = Math.Clamp(O.AccessTokenMinutes, 1, 24 * 60);
        var now = Now;
        db.OAuthTokens.Add(new OAuthToken { GrantId = grantId, Kind = OAuthTokenKind.Access, TokenHash = Hash(access), Scopes = scopes, CreatedAt = now, ExpiresAt = now.AddMinutes(minutes) });
        db.OAuthTokens.Add(new OAuthToken { GrantId = grantId, Kind = OAuthTokenKind.Refresh, TokenHash = Hash(refresh), Scopes = scopes, CreatedAt = now, ExpiresAt = now.AddDays(Math.Clamp(O.RefreshTokenDays, 1, 365)) });
        await db.SaveChangesAsync();
        return new TokenResponse(access, minutes * 60, refresh, ScopeString(scopes));
    }

    // ------------------------------------------------------------------ revocation (RFC 7009)

    /// <summary>
    /// Revokes a token the client holds: a refresh token revokes its whole grant, an access token only itself. Unknown
    /// tokens and tokens of other clients are ignored (the endpoint always answers 200).
    /// </summary>
    public async Task RevokeTokenAsync(string? presented, string? clientId)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(clientId)) return;
        await using var db = await dbFactory.CreateDbContextAsync();
        var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client == null) return;
        var hash = Hash(presented);
        var token = await db.OAuthTokens.Include(t => t.Grant).FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token == null || token.Grant.ClientId != client.Id) return;
        if (token.Kind == OAuthTokenKind.Refresh) await RevokeGrantCoreAsync(db, token.GrantId, "client", null);
        else await db.OAuthTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now));
    }

    /// <summary>The profile page's Revoke: only the grant's own user.</summary>
    public async Task<bool> RevokeGrantAsync(int userId, int grantId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.OAuthGrants.AnyAsync(g => g.Id == grantId && g.UserId == userId && g.RevokedAt == null)) return false;
        await RevokeGrantCoreAsync(db, grantId, "user", userId);
        return true;
    }

    private async Task RevokeGrantCoreAsync(AppDbContext db, int grantId, string reason, int? actorId)
    {
        var now = Now;
        var n = await db.OAuthGrants.Where(g => g.Id == grantId && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, now).SetProperty(g => g.RevokedReason, reason));
        await db.OAuthTokens.Where(t => t.GrantId == grantId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
        if (n == 0) return;
        var g = await db.OAuthGrants.AsNoTracking().Where(x => x.Id == grantId).Select(x => new { x.UserId, x.Client.ClientId, x.Client.ClientName }).FirstAsync();
        log.LogInformation("OAuth grant {Grant} revoked ({Reason})", grantId, reason);
        if (actorId != null)
            await audit.RecordAsync("oauth.grant_revoked", "oauth_grant", grantId, details: new { g.ClientId, g.ClientName, reason }, actorId: actorId);
        else
            await audit.RecordAsync("oauth.grant_revoked", "oauth_grant", grantId, details: new { g.ClientId, g.ClientName, reason, user = g.UserId }, anonymous: true, actorName: "OAuth client");
    }

    // ------------------------------------------------------------------ /mcp

    /// <summary>The access token's user and scopes, or null when it is unknown, expired, revoked, or its user can't sign in.</summary>
    public async Task<McpOAuthPrincipal?> ValidateAccessTokenAsync(string presented)
    {
        if (!O.Enabled || !presented.StartsWith(AccessPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(presented);
        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync();
        var token = await db.OAuthTokens.AsNoTracking().Include(t => t.Grant).ThenInclude(g => g.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == OAuthTokenKind.Access);
        if (token == null || token.RevokedAt != null || token.ExpiresAt <= now || token.Grant.RevokedAt != null) return null;
        if (!UserActive(token.Grant.User, now)) return null;
        if (token.Grant.LastUsedAt is not { } last || now - last > TimeSpan.FromMinutes(1))
            await db.OAuthGrants.Where(g => g.Id == token.GrantId).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now));
        return new McpOAuthPrincipal(token.Grant.User, token.Scopes, token.GrantId, token.Id);
    }

    // ------------------------------------------------------------------ profile

    /// <summary>The user's unrevoked grants that can still be refreshed.</summary>
    public async Task<List<ConnectedApp>> ListConnectedAsync(int userId)
    {
        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.OAuthGrants.AsNoTracking()
            .Where(g => g.UserId == userId && g.RevokedAt == null
                        && db.OAuthTokens.Any(t => t.GrantId == g.Id && t.Kind == OAuthTokenKind.Refresh && t.RotatedAt == null && t.RevokedAt == null && t.ExpiresAt > now))
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new { g.Id, g.Client.ClientName, g.Client.RedirectUris, g.Scopes, g.CreatedAt, g.LastUsedAt }).ToListAsync();
        return rows.Select(r => new ConnectedApp(r.Id, r.ClientName, RedirectDisplay(r.RedirectUris.Split('\n')[0]), r.Scopes, r.CreatedAt, r.LastUsedAt)).ToList();
    }
}
