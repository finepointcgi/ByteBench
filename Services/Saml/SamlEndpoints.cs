using System.Net;
using ByteBench.Services.Sso;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Services.Saml;

/// <summary>
/// SAML 2.0 service provider endpoints:
/// <list type="bullet">
/// <item><c>GET /saml/metadata</c>: SP metadata XML (entity id, ACS, SLO, signing certificate when requests are signed).</item>
/// <item><c>GET /saml/login?returnUrl=</c>: starts SP-initiated sign-in (AuthnRequest over HTTP-Redirect).</item>
/// <item><c>POST /saml/acs</c>: the assertion consumer service (HTTP-POST binding). No antiforgery token: the IdP posts it;
/// the request id cookie and InResponseTo play that role.</item>
/// <item><c>GET /saml/logout</c>: SP-initiated single logout after the local sign-out, and the IdP's LogoutResponse.
/// IdP-initiated logout requests are not supported.</item>
/// </list>
/// IdP-initiated (unsolicited) sign-in is deliberately not supported: without InResponseTo there is nothing that ties a
/// response to a sign-in this browser started (login CSRF) and no request to answer once.
/// </summary>
public static class SamlEndpoints
{
    public static IServiceCollection AddSaml(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SamlConfigStore>();
        services.AddSingleton<SamlMetadataClient>();
        services.AddScoped<SamlService>();
        services.AddScoped<SamlAdminService>();
        return services;
    }

    public static IEndpointRouteBuilder MapSaml(this IEndpointRouteBuilder app)
    {
        var saml = app.MapGroup("/saml").AllowAnonymous().ExcludeFromDescription();

        saml.MapGet("/metadata", async (HttpContext http, SamlConfigStore store, SamlService service) =>
        {
            var o = await store.CurrentAsync();
            if (!o.Enabled) return Results.NotFound();
            return Results.Text(await service.SpMetadataAsync(o, http.Request), "application/samlmetadata+xml; charset=utf-8");
        });

        saml.MapGet("/login", async (HttpContext http, string? returnUrl, SamlService service) =>
        {
            if (await service.ActiveAsync() is not { } o) return Results.Redirect(SsoService.LoginError(SsoErrors.UnknownProvider, null));
            return Results.Redirect(await service.StartLoginAsync(o, http, returnUrl));
        });

        saml.MapPost("/acs", async (HttpContext http, SamlService service) =>
        {
            if (await service.ActiveAsync() is not { } o) return Results.Redirect(SsoService.LoginError(SsoErrors.UnknownProvider, null));
            // 303 See Other: the browser follows with a GET.
            var next = await service.HandleAcsAsync(o, http);
            http.Response.Headers.Location = next;
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        }).DisableAntiforgery();

        saml.MapGet("/logout", async (HttpContext http, SamlService service) =>
        {
            var q = http.Request.Query;
            if (q.ContainsKey("SAMLResponse")) return Results.Redirect("/account/login"); // the IdP finished its logout
            if (q.ContainsKey("SAMLRequest"))
                return Results.Text("IdP-initiated single logout is not supported by ByteBench. Sign out from ByteBench instead.", "text/plain", statusCode: 400);
            // Only after the local sign-out (POST /account/logout); a link can't sign a signed-in user out.
            if (http.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");
            if (await service.ActiveAsync() is not { } o || await service.BuildLogoutRedirectAsync(o, http) is not { } idp) return Results.Redirect("/account/login");
            // A page, not a redirect: this follows the sign-out form's POST, and the CSP's form-action would block a redirect
            // chain to the IdP. A meta refresh is a plain navigation.
            var url = WebUtility.HtmlEncode(idp);
            var title = WebUtility.HtmlEncode(o.Title);
            return Results.Content($"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="refresh" content="0;url={url}">
                <meta name="viewport" content="width=device-width, initial-scale=1"><title>Signing out · ByteBench</title></head>
                <body><p>Signing you out of {title}… <a href="{url}">Continue</a></p></body></html>
                """, "text/html; charset=utf-8");
        });
        return app;
    }
}
