using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Sso;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Tests.Sso;

/// <summary>OIDC and GitHub sign-in end to end against <see cref="MockOidcProvider"/>.</summary>
public class SsoTests(SsoTestApp app) : IClassFixture<SsoTestApp>
{
    private static string NewSub() => "sub-" + Guid.NewGuid().ToString("N")[..10];
    private static string NewEmail(string domain = "example.org") => $"u{Guid.NewGuid():N}"[..12] + "@" + domain;

    private async Task<User?> FindByEmailAsync(string email)
    {
        await using var s = app.Scope();
        return await s.Get<UserManager<User>>().FindByEmailAsync(email);
    }

    private async Task<IList<UserLoginInfo>> LoginsAsync(int userId)
    {
        await using var s = app.Scope();
        var users = s.Get<UserManager<User>>();
        return await users.GetLoginsAsync((await users.FindByIdAsync(userId.ToString()))!);
    }

    private List<AuditEvent> Audit(string action, int? targetUser = null)
    {
        using var db = app.Db();
        return db.AuditEvents.Where(e => e.Action == action && (targetUser == null || e.TargetId == targetUser.ToString())).ToList();
    }

    [Fact]
    public async Task First_sign_in_creates_a_passwordless_account_and_signs_in()
    {
        var browser = app.Browser();
        var email = NewEmail();
        var location = await app.SsoAsync(browser, "/account/sso/mock?returnUrl=%2Fexplore", new MockUser(NewSub(), email, Name: "Nia Nakamura", UserName: "nia.n"));
        Assert.Equal("/explore", location);

        // The authorization request used PKCE (S256), state and nonce; the token request's verifier matched.
        Assert.Equal("S256", app.Idp.LastAuthorize["code_challenge_method"]);
        Assert.True(app.Idp.LastAuthorize.ContainsKey("state") && app.Idp.LastAuthorize.ContainsKey("nonce"));
        Assert.Equal(0, app.Idp.PkceFailures);

        var user = (await FindByEmailAsync(email))!;
        Assert.StartsWith("nia.n", user.UserName);
        Assert.Equal("Nia Nakamura", user.DisplayName);
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
        Assert.Contains(await LoginsAsync(user.Id), l => l.LoginProvider == "mock");
        Assert.Single(Audit("auth.sso_sign_in", user.Id));

        // Signed in: the profile works and says there's no password.
        var profile = await browser.GetAsync("/account/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var html = await profile.Content.ReadAsStringAsync();
        Assert.Contains("no ByteBench password", html);
        Assert.Contains("Connected accounts", html);

        // Second sign-in reuses the link (no new account).
        var again = await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser((await LoginsAsync(user.Id)).First().ProviderKey, email));
        Assert.Equal("/", again);
        using var db = app.Db();
        Assert.Equal(1, db.Users.Count(u => u.Email == email));
    }

    [Fact]
    public async Task Verified_email_links_an_existing_confirmed_account()
    {
        var local = await app.CreateUserAsync("link" + Guid.NewGuid().ToString("N")[..6]); // EmailConfirmed
        var location = await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), local.Email, EmailVerified: true));
        Assert.Equal("/", location);
        Assert.Contains(await LoginsAsync(local.Id), l => l.LoginProvider == "mock");
        Assert.Single(Audit("auth.sso_link", local.Id));
    }

    [Fact]
    public async Task Unverified_email_never_takes_over_or_creates_an_account()
    {
        var local = await app.CreateUserAsync("unv" + Guid.NewGuid().ToString("N")[..6]);
        var location = await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), local.Email, EmailVerified: false));
        Assert.Equal("/account/login?sso_error=unverified&provider=mock", location);
        Assert.Empty(await LoginsAsync(local.Id));

        var fresh = NewEmail();
        location = await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), fresh, EmailVerified: false));
        Assert.Contains("sso_error=unverified", location);
        Assert.Null(await FindByEmailAsync(fresh));
        Assert.Contains(Audit("auth.sso_failed"), e => e.Details!.Contains("unverified"));

        // The sign-in page explains it.
        var page = await app.Browser().GetStringAsync(location);
        Assert.Contains("isn&#x27;t verified", page);
    }

    [Fact]
    public async Task Verified_email_does_not_link_an_unconfirmed_local_account()
    {
        // Someone registered with this address (unconfirmed, with a password) before its owner used SSO.
        var email = NewEmail();
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            var squatter = new User { UserName = "sq" + Guid.NewGuid().ToString("N")[..6], Email = email, DisplayName = "Squatter" };
            Assert.True((await users.CreateAsync(squatter, SeedData.DemoPassword)).Succeeded);
        }
        var location = await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), email));
        Assert.Contains("sso_error=exists", location);
        var squatterUser = (await FindByEmailAsync(email))!;
        Assert.Empty(await LoginsAsync(squatterUser.Id));
    }

    [Fact]
    public async Task Email_domain_allow_list()
    {
        var outside = await app.SsoAsync(app.Browser(), "/account/sso/corp", new MockUser(NewSub(), NewEmail("other.example")));
        Assert.Equal("/account/login?sso_error=domain&provider=corp", outside);

        var inside = NewEmail("corp.example");
        Assert.Equal("/", await app.SsoAsync(app.Browser(), "/account/sso/corp", new MockUser(NewSub(), inside)));
        Assert.NotNull(await FindByEmailAsync(inside));

        // An unverified address in the right domain doesn't count.
        var unverified = await app.SsoAsync(app.Browser(), "/account/sso/corp", new MockUser(NewSub(), NewEmail("corp.example"), EmailVerified: false));
        Assert.Contains("sso_error=unverified", unverified);
    }

    [Fact]
    public async Task Admin_group_grants_and_revokes_only_its_own_grant()
    {
        var sub = NewSub();
        var email = NewEmail();
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: ["gb-admins"]));
        var user = (await FindByEmailAsync(email))!;
        Assert.True(user.IsAdmin);
        Assert.Equal("oidc:mock", user.AdminGrantedBy);

        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: []));
        Assert.False((await FindByEmailAsync(email))!.IsAdmin);

        // alice is an admin by hand: signing in through the provider without the admin group keeps her admin.
        Assert.Equal("/", await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), "alice@example.com")));
        Assert.True(app.User("alice").IsAdmin);
    }

    [Fact]
    public async Task Group_claims_map_to_group_memberships()
    {
        var group = await app.EnsureTeamGroupAsync();
        var sub = NewSub();
        var email = NewEmail();
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: ["engineering", "leads"]));
        var user = (await FindByEmailAsync(email))!;
        GroupMember? Member()
        {
            using var db = app.Db();
            return db.GroupMembers.AsNoTracking().FirstOrDefault(m => m.GroupId == group.Id && m.UserId == user.Id);
        }
        Assert.Equal(ProjectRole.Maintainer, Member()!.Role); // the highest mapped role

        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: ["engineering"]));
        Assert.Equal(ProjectRole.Developer, Member()!.Role);

        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: ["sales"]));
        Assert.Null(Member());
        using (var db = app.Db()) Assert.False(db.ExternalGroupMemberships.Any(m => m.UserId == user.Id));

        // A membership added by hand isn't the provider's to change.
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<GroupService>().AddMemberAsync(group.Id, user.UserName!, ProjectRole.Viewer);
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: ["leads"]));
        Assert.Equal(ProjectRole.Viewer, Member()!.Role);
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Groups: []));
        Assert.Equal(ProjectRole.Viewer, Member()!.Role);
    }

    [Fact]
    public async Task Deactivated_and_locked_accounts_are_refused()
    {
        var sub = NewSub();
        var email = NewEmail();
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email));
        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<AdminUserService>().DeactivateAsync((await FindByEmailAsync(email))!.Id);

        var location = await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email));
        Assert.Equal("/account/login?sso_error=locked&provider=mock", location);
    }

    [Fact]
    public async Task Two_factor_users_enter_their_code_unless_the_idp_did_mfa()
    {
        var sub = NewSub();
        var email = NewEmail();
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email));
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            var u = (await users.FindByEmailAsync(email))!;
            await users.ResetAuthenticatorKeyAsync(u);
            await users.SetTwoFactorEnabledAsync(u, true);
        }

        var browser = app.Browser();
        var location = await app.SsoAsync(browser, "/account/sso/mock", new MockUser(sub, email, Amr: ["pwd"]));
        Assert.StartsWith("/account/login-2fa", location);
        var page = await browser.GetStringAsync(location);
        Assert.Contains("Authentication code", page); // the two-factor step knows who's signing in

        // The "mock" provider trusts the IdP's MFA (TwoFactor: TrustIdp).
        Assert.Equal("/", await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, email, Amr: ["pwd", "mfa"])));
    }

    [Fact]
    public async Task Without_auto_create_only_existing_accounts_can_sign_in()
    {
        var fresh = NewEmail();
        var location = await app.SsoAsync(app.Browser(), "/account/sso/invite", new MockUser(NewSub(), fresh));
        Assert.Equal("/account/login?sso_error=no_account&provider=invite", location);
        Assert.Null(await FindByEmailAsync(fresh));
    }

    [Fact]
    public async Task Forged_callbacks_and_unknown_providers_are_refused()
    {
        var browser = app.Browser();
        var forged = await browser.GetAsync("/signin-sso/mock?code=stolen&state=not-ours");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Equal("/account/login?sso_error=failed&provider=mock", forged.Headers.Location!.ToString());

        var unknown = await browser.GetAsync("/account/sso/nope");
        Assert.Contains("sso_error=unknown_provider", unknown.Headers.Location!.ToString());

        // Without the external cookie, the completion endpoint does nothing.
        var complete = await browser.GetAsync("/account/sso/complete");
        Assert.Contains("sso_error=failed", complete.Headers.Location!.ToString());

        // returnUrl can't send the user off-site.
        var offsite = await app.SsoAsync(app.Browser(), "/account/sso/mock?returnUrl=https%3A%2F%2Fevil.example%2F", new MockUser(NewSub(), NewEmail()));
        Assert.Equal("/", offsite);
    }

    [Fact]
    public async Task Link_and_unlink_from_the_profile()
    {
        var carol = await app.CreateUserAsync("lk" + Guid.NewGuid().ToString("N")[..6]);
        var browser = await app.CookieClientAsync(carol.UserName!);

        // Without the page's token the link is refused (another site can't start it).
        var noToken = await browser.GetAsync("/account/sso/mock/link");
        Assert.Contains("sso_error=expired", noToken.Headers.Location!.ToString());

        var profile = await browser.GetStringAsync("/account/profile");
        var link = WebUtility.HtmlDecode(Regex.Match(profile, "href=\"(account/sso/mock/link\\?t=[^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(link);
        // The IdP account has a different email: linking from the profile doesn't depend on it.
        var sub = NewSub();
        var location = await app.SsoAsync(browser, "/" + link, new MockUser(sub, NewEmail(), EmailVerified: false));
        Assert.Equal("/account/profile?sso=linked&provider=mock#connected-accounts", location);
        Assert.Contains(await LoginsAsync(carol.Id), l => l.LoginProvider == "mock" && l.ProviderKey == sub);
        Assert.Contains(Audit("auth.sso_link", carol.Id), e => e.Details!.Contains("profile"));

        // Now the IdP account signs in as carol.
        Assert.Equal("/", await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(sub, null)));

        // Another user can't link the same IdP account.
        var dan = await app.CreateUserAsync("lk" + Guid.NewGuid().ToString("N")[..6]);
        var danBrowser = await app.CookieClientAsync(dan.UserName!);
        var danProfile = await danBrowser.GetStringAsync("/account/profile");
        var danLink = WebUtility.HtmlDecode(Regex.Match(danProfile, "href=\"(account/sso/mock/link\\?t=[^\"]+)\"").Groups[1].Value);
        Assert.Contains("sso_error=linked_elsewhere", await app.SsoAsync(danBrowser, "/" + danLink, new MockUser(sub, null)));

        // Disconnect (carol has a password, so it's allowed).
        var page = await browser.GetStringAsync("/account/profile");
        var unlink = await browser.PostAsync("/account/profile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "unlink-sso", ["__RequestVerificationToken"] = SsoTestApp.Token(page), ["UnlinkProvider"] = "mock",
        }));
        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);
        Assert.Contains("was disconnected", await unlink.Content.ReadAsStringAsync());
        Assert.Empty(await LoginsAsync(carol.Id));
        Assert.Single(Audit("auth.sso_unlink", carol.Id));
    }

    [Fact]
    public async Task A_passwordless_account_keeps_its_last_login()
    {
        var email = NewEmail();
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), email));
        var user = (await FindByEmailAsync(email))!;
        await using var s = app.Scope();
        Assert.Equal(SsoErrors.LastLogin, await s.Get<SsoService>().UnlinkAsync(user.Id, "mock"));
        Assert.Single(await LoginsAsync(user.Id));
    }

    [Fact]
    public async Task GitHub_sign_in_uses_the_verified_email_and_teams()
    {
        var group = await app.EnsureTeamGroupAsync();
        var email = NewEmail();
        var location = await app.SsoAsync(app.Browser(), "/account/sso/gh", new MockUser("4242" + Random.Shared.Next(1000, 9999), email,
            Name: "Octo Cat", UserName: "octocat", Groups: ["acme", "acme/devs"]));
        Assert.Equal("/", location);
        Assert.StartsWith("code_challenge", string.Join(",", app.Idp.LastAuthorize.Keys.Where(k => k.StartsWith("code_challenge"))));
        var user = (await FindByEmailAsync(email))!;
        Assert.StartsWith("octocat", user.UserName);
        Assert.Contains(await LoginsAsync(user.Id), l => l.LoginProvider == "gh");
        using var db = app.Db();
        Assert.Equal(ProjectRole.Developer, db.GroupMembers.Single(m => m.GroupId == group.Id && m.UserId == user.Id).Role);

        // An unverified GitHub email can't create an account.
        var unverified = await app.SsoAsync(app.Browser(), "/account/sso/gh", new MockUser("777" + Random.Shared.Next(1000, 9999), NewEmail(), EmailVerified: false, UserName: "x"));
        Assert.Contains("sso_error=unverified", unverified);
    }

    [Fact]
    public async Task Login_page_shows_provider_links_and_csp_allows_the_idp()
    {
        var res = await app.Browser().GetAsync("/account/login?returnUrl=%2Fexplore");
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("href=\"account/sso/mock?returnUrl=%2Fexplore\"", html);
        Assert.Contains("Sign in with Mock IdP", html);
        // A full navigation, not Blazor's enhanced fetch (which connect-src would block at the redirect to the IdP).
        Assert.Matches("href=\"account/sso/mock\\?returnUrl=%2Fexplore\"[^>]*data-enhance-nav=\"false\"|data-enhance-nav=\"false\"[^>]*href=\"account/sso/mock", html);
        Assert.Contains("Sign in with GitHub", html);
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        var formAction = Regex.Match(csp, "form-action ([^;]+)").Groups[1].Value.Split(' ');
        Assert.Contains("'self'", formAction);
        Assert.Contains(new Uri(app.Idp.BaseUrl).GetLeftPart(UriPartial.Authority), formAction);
    }

    [Fact]
    public async Task Sso_accounts_use_tokens_for_git()
    {
        var email = NewEmail();
        await app.SsoAsync(app.Browser(), "/account/sso/mock", new MockUser(NewSub(), email, UserName: "gitsso"));
        var user = (await FindByEmailAsync(email))!;
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, (user.UserName!, ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id);
        var url = $"/git/{project.Key}/{repo.Slug}.git/info/refs?service=git-upload-pack";

        async Task<HttpStatusCode> Git(string secret)
        {
            var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user.UserName}:{secret}")));
            return (await client.GetAsync(url)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Git(""));
        Assert.Equal(HttpStatusCode.Unauthorized, await Git("any-password"));
        Assert.Equal(HttpStatusCode.OK, await Git(await app.CreateTokenAsync(user.UserName!, TokenScopes.ReadRepository)));
    }
}
