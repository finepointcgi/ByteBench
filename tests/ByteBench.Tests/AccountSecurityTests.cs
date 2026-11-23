using System.Diagnostics;
using System.Net;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Accounts;
using ByteBench.Services.Ai;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>Forgot password / reset, at service level and through the real pages.</summary>
public class PasswordResetTests(DefaultAccountApp app) : IClassFixture<DefaultAccountApp>
{
    private const string NewPassword = "Brand-new-pass1";

    private async Task<(User User, string Token)> UserWithTokenAsync()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("rs"));
        await using var s = app.Scope();
        var users = s.Get<UserManager<User>>();
        var fresh = (await users.FindByIdAsync(user.Id.ToString()))!;
        return (fresh, await users.GeneratePasswordResetTokenAsync(fresh));
    }

    private async Task<ResetResult> ResetAsync(User user, string token, string password)
    {
        await using var s = app.Scope();
        return await s.Get<PasswordResetService>().ResetAsync(user.Id, token, password, "198.51.100.1");
    }

    private async Task<bool> RequestAsync(string email, string ip = "203.0.113.5")
    {
        await using var s = app.Scope();
        return await s.Get<PasswordResetService>().RequestAsync(email, ip);
    }

    [Fact]
    public async Task A_request_for_an_account_emails_one_link_and_an_unknown_address_gets_the_same_answer()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("rq"));
        Assert.True(await RequestAsync(user.Email!));
        Assert.True(await RequestAsync("nobody-" + Guid.NewGuid().ToString("N")[..6] + "@example.com"));

        var mail = Assert.Single(app.Mail.To(user.Email!));
        Assert.Contains("/account/reset-password?uid=" + user.Id, CapturingMailer.LinkOf(mail));
        Assert.Contains("expires in 1 hour", mail.Text);
        Assert.DoesNotContain(app.Mail.All, m => m.To.StartsWith("nobody-"));
        using var db = app.Db();
        Assert.Contains(db.AuditEvents, e => e.Action == "auth.password_reset_requested" && e.TargetId == user.Id.ToString());
    }

    [Fact]
    public async Task The_reset_link_works_once_enforces_the_password_policy_and_signs_other_sessions_out()
    {
        var (user, token) = await UserWithTokenAsync();
        var stampBefore = user.SecurityStamp;

        // The policy applies; a rejected password doesn't use up the link.
        var weak = await ResetAsync(user, token, "short");
        Assert.Equal(ResetOutcome.WeakPassword, weak.Outcome);
        Assert.NotEmpty(weak.Errors);
        var noDigit = await ResetAsync(user, token, "Nodigitshere");
        Assert.Equal(ResetOutcome.WeakPassword, noDigit.Outcome);

        // A brute-force lockout is cleared by a reset.
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            var u = (await users.FindByIdAsync(user.Id.ToString()))!;
            await users.SetLockoutEndDateAsync(u, DateTimeOffset.UtcNow.AddMinutes(4));
            await users.AccessFailedAsync(u);
        }

        Assert.Equal(ResetOutcome.Done, (await ResetAsync(user, token, NewPassword)).Outcome);

        await using var check = app.Scope();
        var m = check.Get<UserManager<User>>();
        var after = (await m.FindByIdAsync(user.Id.ToString()))!;
        Assert.True(await m.CheckPasswordAsync(after, NewPassword));
        Assert.False(await m.CheckPasswordAsync(after, SeedData.DemoPassword));
        Assert.NotEqual(stampBefore, after.SecurityStamp); // every cookie issued before this is refused at the next stamp check
        Assert.False(await m.IsLockedOutAsync(after));
        Assert.Equal(0, await m.GetAccessFailedCountAsync(after));

        // Reuse, even with a valid new password, is refused.
        Assert.Equal(ResetOutcome.InvalidLink, (await ResetAsync(user, token, "Another-pass-2")).Outcome);
        using var db = app.Db();
        Assert.Contains(db.AuditEvents, e => e.Action == "auth.password_reset" && e.TargetId == user.Id.ToString());
        var notice = Assert.Single(app.Mail.To(user.Email!));
        Assert.Contains("password was just changed", notice.Text);
    }

    [Fact]
    public async Task A_forged_link_and_a_link_for_another_user_are_refused()
    {
        var (a, tokenA) = await UserWithTokenAsync();
        var (b, _) = await UserWithTokenAsync();
        Assert.Equal(ResetOutcome.InvalidLink, (await ResetAsync(b, tokenA, NewPassword)).Outcome);
        Assert.Equal(ResetOutcome.InvalidLink, (await ResetAsync(a, tokenA + "x", NewPassword)).Outcome);
        Assert.Equal(ResetOutcome.InvalidLink, (await ResetAsync(a, "", NewPassword)).Outcome);
        Assert.Equal(ResetOutcome.InvalidLink, (await ResetAsync(new User { Id = 987654 }, tokenA, NewPassword)).Outcome);
    }

    [Fact]
    public async Task Accounts_that_cant_be_reset_by_email_get_no_email_but_the_same_answer()
    {
        var sso = await app.CreateUserWithoutPasswordAsync(AccountTestApp.NewName("sso"));
        var deactivated = await app.CreateUserAsync(AccountTestApp.NewName("deact"));
        var locked = await app.CreateUserAsync(AccountTestApp.NewName("lock"));
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var admin = s.Get<AdminUserService>();
            await admin.DeactivateAsync(deactivated.Id);
            await admin.LockAsync(locked.Id, DateTimeOffset.UtcNow.AddHours(2));
        }
        // A system account with a password still isn't reset.
        int botId;
        await using (var s = app.Scope())
        {
            await using var db = app.Db();
            botId = await AiBot.EnsureAsync(db);
            var users = s.Get<UserManager<User>>();
            await users.AddPasswordAsync((await users.FindByIdAsync(botId.ToString()))!, "Bot-password-1");
        }
        var bot = app.Db().Users.AsNoTracking().First(u => u.Id == botId);

        foreach (var email in new[] { sso.Email!, deactivated.Email!, locked.Email!, bot.Email! })
        {
            Assert.True(await RequestAsync(email));
            Assert.Empty(app.Mail.To(email));
        }

        // The tokens they might hold are refused too.
        await using var scope = app.Scope();
        var m = scope.Get<UserManager<User>>();
        var lockedFresh = (await m.FindByIdAsync(locked.Id.ToString()))!;
        Assert.Equal(ResetOutcome.InvalidLink, (await ResetAsync(lockedFresh, await m.GeneratePasswordResetTokenAsync(lockedFresh), NewPassword)).Outcome);
    }

    [Fact]
    public async Task A_brute_force_lockout_does_not_stop_a_reset_request()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("bf"));
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            await users.SetLockoutEndDateAsync((await users.FindByIdAsync(user.Id.ToString()))!, DateTimeOffset.UtcNow.AddMinutes(5));
        }
        Assert.True(await RequestAsync(user.Email!));
        Assert.Single(app.Mail.To(user.Email!));
    }

    [Fact]
    public async Task Reset_links_use_their_own_lifespan_option()
    {
        await using var s = app.Scope();
        Assert.Equal(TimeSpan.FromMinutes(60), s.Get<IOptions<PasswordResetTokenProviderOptions>>().Value.TokenLifespan);
    }

    [Fact]
    public async Task The_whole_flow_works_through_the_pages_and_two_factor_still_applies()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("flow"));
        // Turn on two-factor for this account.
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            await users.SetTwoFactorEnabledAsync((await users.FindByIdAsync(user.Id.ToString()))!, true);
        }
        var browser = app.Browser();

        // The sign-in page links to the request page.
        Assert.Contains("account/forgot-password", await browser.GetStringAsync("/account/login"));

        var (ask, askBody) = await AccountTestApp.PostFormAsync(browser, "/account/forgot-password", "forgot-password", ("Input.Email", user.Email!));
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Contains("If that email address belongs to an account", askBody);
        var link = CapturingMailer.LinkOf(Assert.Single(app.Mail.To(user.Email!)));

        var form = await browser.GetStringAsync(link);
        Assert.Contains("Choose a new password", form);
        Assert.Contains("rp-password", form);

        var (done, _) = await AccountTestApp.PostFormAsync(browser, link, "reset-password", ("Input.Password", NewPassword), ("Input.ConfirmPassword", NewPassword));
        Assert.True(AccountTestApp.IsRedirect(done));
        Assert.Contains("account/login?notice=reset", done.Headers.Location!.ToString());

        // The link is dead now.
        Assert.Contains("invalid, expired or was already used", await browser.GetStringAsync(link));

        // The old password fails; the new one passes the password step but still needs the authenticator code.
        var old = await app.LoginAsync(app.Browser(), user.UserName!, SeedData.DemoPassword);
        Assert.Contains("Invalid username/email or password", old.Body);
        var fresh = await app.LoginAsync(app.Browser(), user.UserName!, NewPassword);
        Assert.True(AccountTestApp.IsRedirect(fresh.Response));
        Assert.Contains("account/login-2fa", fresh.Response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_request_page_answers_the_same_for_known_and_unknown_addresses()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("same"));
        var known = await AccountTestApp.PostFormAsync(app.Browser(), "/account/forgot-password", "forgot-password", ("Input.Email", user.Email!));
        var unknown = await AccountTestApp.PostFormAsync(app.Browser(), "/account/forgot-password", "forgot-password", ("Input.Email", "ghost-" + Guid.NewGuid().ToString("N")[..6] + "@example.org"));
        Assert.Equal(known.Response.StatusCode, unknown.Response.StatusCode);
        string Alert(string body)
        {
            var i = body.IndexOf("If that email address", StringComparison.Ordinal);
            Assert.True(i > 0);
            return body[i..body.IndexOf("</div>", i, StringComparison.Ordinal)];
        }
        Assert.Equal(Alert(known.Body), Alert(unknown.Body));
    }
}

public class PasswordResetLimitsTests(ThrottledAccountApp app) : IClassFixture<ThrottledAccountApp>
{
    [Fact]
    public async Task Per_account_limit_is_silent_and_per_ip_limit_is_visible()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("lim"));
        for (var i = 0; i < 4; i++)
        {
            await using var s = app.Scope();
            Assert.True(await s.Get<PasswordResetService>().RequestAsync(user.Email, "192.0.2." + (10 + i))); // every request looks the same...
        }
        Assert.Equal(2, app.Mail.To(user.Email!).Count); // ...but only Account:MaxRequestsPerAccount emails go out

        // One IP: five requests pass, the sixth is refused, whatever the address.
        await using var scope = app.Scope();
        var reset = scope.Get<PasswordResetService>();
        for (var i = 0; i < 5; i++) Assert.True(await reset.RequestAsync($"x{i}@example.com", "192.0.2.99"));
        Assert.False(await reset.RequestAsync("x9@example.com", "192.0.2.99"));
        Assert.True(await reset.RequestAsync("x9@example.com", "192.0.2.100")); // another client is fine
    }

    [Fact]
    public async Task The_request_page_shows_the_limit_and_uses_the_configured_lifespan()
    {
        var browser = app.Browser();
        string? last = null;
        for (var i = 0; i < 6; i++)
            last = (await AccountTestApp.PostFormAsync(browser, "/account/forgot-password", "forgot-password", ("Input.Email", $"p{i}@example.com"))).Body;
        Assert.Contains("Too many requests", last);

        await using var s = app.Scope();
        Assert.Equal(TimeSpan.FromMinutes(90), s.Get<IOptions<PasswordResetTokenProviderOptions>>().Value.TokenLifespan);
    }
}

public class ExpiredResetLinkTests(ExpiredLinkApp app) : IClassFixture<ExpiredLinkApp>
{
    [Fact]
    public async Task An_expired_link_is_refused_and_the_page_says_so()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("exp"));
        await using var s = app.Scope();
        var users = s.Get<UserManager<User>>();
        var token = await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(user.Id.ToString()))!);
        var reset = s.Get<PasswordResetService>();
        Assert.False(await reset.IsLinkValidAsync(user.Id, token));
        Assert.Equal(ResetOutcome.InvalidLink, (await reset.ResetAsync(user.Id, token, "Fresh-password-1", "1.1.1.1")).Outcome);
        Assert.Contains("invalid, expired", await app.Browser().GetStringAsync($"/account/reset-password?uid={user.Id}&token={Uri.EscapeDataString(token)}"));
    }
}

public class ResponseFloorTests(FloorAccountApp app) : IClassFixture<FloorAccountApp>
{
    [Fact]
    public async Task Known_and_unknown_addresses_take_at_least_the_floor()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("floor"));
        foreach (var email in new[] { user.Email!, "ghost@example.org" })
        {
            await using var s = app.Scope();
            var sw = Stopwatch.StartNew();
            await s.Get<PasswordResetService>().RequestAsync(email, "198.18.0.1");
            Assert.True(sw.ElapsedMilliseconds >= 190, $"{email} answered in {sw.ElapsedMilliseconds} ms");
        }
    }
}

/// <summary>Account:RequireEmailConfirmation on: registration, sign-in, resend, address changes.</summary>
public class EmailConfirmationTests(ConfirmAccountApp app) : IClassFixture<ConfirmAccountApp>
{
    private async Task<(string UserName, string Email)> RegisterAsync(HttpClient browser)
    {
        var name = AccountTestApp.NewName("reg");
        var (response, body) = await AccountTestApp.PostFormAsync(browser, "/account/register", "register",
            ("Input.UserName", name), ("Input.DisplayName", name), ("Input.Email", name + "@example.com"),
            ("Input.Password", SeedData.DemoPassword), ("Input.ConfirmPassword", SeedData.DemoPassword));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // no redirect: not signed in
        Assert.Contains("We sent a confirmation link", body);
        return (name, name + "@example.com");
    }

    [Fact]
    public async Task A_new_registration_must_confirm_before_signing_in()
    {
        var browser = app.Browser();
        var (name, email) = await RegisterAsync(browser);

        var blocked = await app.LoginAsync(app.Browser(), name, SeedData.DemoPassword);
        Assert.Equal(HttpStatusCode.OK, blocked.Response.StatusCode);
        Assert.Contains("Confirm your email address before signing in", blocked.Body);
        // A wrong password doesn't reveal that the account exists but is unconfirmed.
        Assert.Contains("Invalid username/email or password", (await app.LoginAsync(app.Browser(), name, "Wrong-pass-1")).Body);

        var mail = Assert.Single(app.Mail.To(email));
        var link = CapturingMailer.LinkOf(mail);
        Assert.Contains("/account/confirm-email?uid=", link);
        Assert.Contains("Your email address is confirmed", await app.Browser().GetStringAsync(link));

        var ok = await app.LoginAsync(app.Browser(), name, SeedData.DemoPassword);
        Assert.True(AccountTestApp.IsRedirect(ok.Response));
        Assert.True(app.User(name).EmailConfirmed);
        using var db = app.Db();
        Assert.Contains(db.AuditEvents, e => e.Action == "auth.email_confirmed" && e.TargetId == app.UserId(name).ToString());
    }

    [Fact]
    public async Task A_tampered_confirmation_link_does_nothing()
    {
        var (name, email) = await RegisterAsync(app.Browser());
        var link = CapturingMailer.LinkOf(Assert.Single(app.Mail.To(email)));
        Assert.Contains("invalid or has expired", await app.Browser().GetStringAsync(link + "x"));
        Assert.False(app.User(name).EmailConfirmed);
    }

    [Fact]
    public async Task Seeded_and_directory_accounts_keep_working()
    {
        var alice = await app.LoginAsync(app.Browser(), "alice", SeedData.DemoPassword);
        Assert.True(AccountTestApp.IsRedirect(alice.Response));
        Assert.True(app.User("alice").EmailConfirmed);
    }

    [Fact]
    public async Task Resend_is_neutral_rate_limited_and_only_for_unconfirmed_accounts()
    {
        var (name, email) = await RegisterAsync(app.Browser());
        var browser = app.Browser();
        var before = app.Mail.To(email).Count;
        var known = await AccountTestApp.PostFormAsync(browser, "/account/resend-confirmation", "resend-confirmation", ("Input.Email", email));
        var unknown = await AccountTestApp.PostFormAsync(browser, "/account/resend-confirmation", "resend-confirmation", ("Input.Email", "ghost@example.org"));
        Assert.Contains("a new link is on its way", known.Body);
        Assert.Contains("a new link is on its way", unknown.Body);
        Assert.Equal(before + 1, app.Mail.To(email).Count);
        Assert.Empty(app.Mail.To("ghost@example.org"));

        // Already confirmed: nothing is sent.
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<AdminUserService>().MarkEmailConfirmedAsync(app.UserId(name));
        var count = app.Mail.To(email).Count;
        await AccountTestApp.PostFormAsync(browser, "/account/resend-confirmation", "resend-confirmation", ("Input.Email", email));
        Assert.Equal(count, app.Mail.To(email).Count);
    }

    [Fact]
    public async Task Admins_can_mark_an_address_confirmed_and_others_cannot()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("adm"));
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            var u = (await users.FindByIdAsync(user.Id.ToString()))!;
            u.EmailConfirmed = false;
            await users.UpdateAsync(u);
        }
        await using (var s = app.Scope(app.UserId("bob")))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<AdminUserService>().MarkEmailConfirmedAsync(user.Id));
        Assert.False(app.User(user.UserName!).EmailConfirmed);

        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<AdminUserService>().MarkEmailConfirmedAsync(user.Id);
        Assert.True(app.User(user.UserName!).EmailConfirmed);
        using var db = app.Db();
        Assert.Contains(db.AuditEvents, e => e.Action == "admin.user_email_confirmed" && e.TargetId == user.Id.ToString());
        var bot = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var s = app.Scope(app.UserId("alice"));
            await using var d = app.Db();
            await s.Get<AdminUserService>().MarkEmailConfirmedAsync(await AiBot.EnsureAsync(d));
        });
        Assert.Contains("system bot", bot.Message);
    }

    [Fact]
    public async Task Changing_the_address_needs_the_new_one_confirmed()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("chg"));
        var newEmail = AccountTestApp.NewName("new") + "@example.com";
        await using var s = app.Scope();
        var users = s.Get<UserManager<User>>();
        var fresh = (await users.FindByIdAsync(user.Id.ToString()))!;
        var confirmation = s.Get<EmailConfirmationService>();

        Assert.Null(await confirmation.RequestChangeAsync(fresh, newEmail));
        Assert.Equal(user.Email, (await users.FindByIdAsync(user.Id.ToString()))!.Email); // unchanged until confirmed
        var link = CapturingMailer.LinkOf(Assert.Single(app.Mail.To(newEmail)));
        var query = System.Web.HttpUtility.ParseQueryString(link[(link.IndexOf('?') + 1)..]);
        var token = query["token"]!;

        Assert.False(await confirmation.ConfirmChangeAsync(user.Id, "someone-else@example.com", token)); // bound to the address
        Assert.False(await confirmation.ConfirmChangeAsync(user.Id, newEmail, token + "x"));
        Assert.True(await confirmation.ConfirmChangeAsync(user.Id, newEmail, token));
        var changed = (await users.FindByIdAsync(user.Id.ToString()))!;
        Assert.Equal(newEmail, changed.Email);
        Assert.True(changed.EmailConfirmed);

        // An address another account owns is refused up front.
        Assert.Contains("already in use", await confirmation.RequestChangeAsync(changed, "alice@example.com") ?? "");
    }

    [Fact]
    public async Task The_profile_page_sends_the_confirmation_instead_of_changing_the_address()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("prof"));
        var browser = await app.CookieClientAsync(user.UserName!);
        var newEmail = AccountTestApp.NewName("p2") + "@example.com";
        var (_, body) = await AccountTestApp.PostFormAsync(browser, "/account/profile", "profile",
            ("ProfileInput.DisplayName", "Renamed"), ("ProfileInput.Email", newEmail), ("ProfileInput.Color", "#123456"));
        Assert.Contains("We sent a confirmation link", body);
        Assert.Equal(user.Email, app.User(user.UserName!).Email);
        Assert.Equal("Renamed", app.User(user.UserName!).DisplayName);
        Assert.Single(app.Mail.To(newEmail));
    }
}

public class EmailConfirmationOffTests(DefaultAccountApp app) : IClassFixture<DefaultAccountApp>
{
    [Fact]
    public async Task With_the_switch_off_registration_signs_in_at_once_and_unconfirmed_users_sign_in()
    {
        var name = AccountTestApp.NewName("off");
        var browser = app.Browser();
        var (response, _) = await AccountTestApp.PostFormAsync(browser, "/account/register", "register",
            ("Input.UserName", name), ("Input.DisplayName", name), ("Input.Email", name + "@example.com"),
            ("Input.Password", SeedData.DemoPassword), ("Input.ConfirmPassword", SeedData.DemoPassword));
        Assert.True(AccountTestApp.IsRedirect(response));
        Assert.Empty(app.Mail.To(name + "@example.com"));
        Assert.False(app.User(name).EmailConfirmed);
        Assert.True(AccountTestApp.IsRedirect((await app.LoginAsync(app.Browser(), name, SeedData.DemoPassword)).Response));
    }
}

/// <summary>Registration control: closed, invitations, domains, precedence, admin creation, SSO.</summary>
public class RegistrationControlTests(ClosedAccountApp app) : IClassFixture<ClosedAccountApp>
{
    private Task<(HttpResponseMessage Response, string Body)> RegisterAsync(HttpClient browser, string name, string? email = null, string url = "/account/register") =>
        AccountTestApp.PostFormAsync(browser, url, "register", ("Input.UserName", name), ("Input.DisplayName", name), ("Input.Email", email ?? name + "@example.com"),
            ("Input.Password", SeedData.DemoPassword), ("Input.ConfirmPassword", SeedData.DemoPassword));

    private bool Exists(string name) { using var db = app.Db(); return db.Users.Any(u => u.UserName == name); }

    [Fact]
    public async Task A_closed_server_shows_a_message_and_refuses_posts()
    {
        var page = await app.Browser().GetStringAsync("/account/register");
        Assert.Contains("Registration is closed on this server", page);
        Assert.DoesNotContain("reg-password", page);
        Assert.DoesNotContain("Create an account", await app.Browser().GetStringAsync("/account/login"));

        // A post made from a stale page (or by a script) is refused as well.
        var open = await app.Browser().GetStringAsync("/account/register");
        var name = AccountTestApp.NewName("closed");
        var response = await app.Browser().PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "register", ["__RequestVerificationToken"] = AccountTestApp.Token(open),
            ["Input.UserName"] = name, ["Input.DisplayName"] = name, ["Input.Email"] = name + "@example.com",
            ["Input.Password"] = SeedData.DemoPassword, ["Input.ConfirmPassword"] = SeedData.DemoPassword,
        }));
        Assert.False(Exists(name));
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Admins_can_still_create_users_and_non_admins_cannot()
    {
        var name = AccountTestApp.NewName("made");
        TemporaryPassword made;
        await using (var s = app.Scope(app.UserId("alice")))
            made = await s.Get<AdminUserService>().CreateUserAsync(name, "Made User", name + "@example.com", isAdmin: false);
        var user = app.User(name);
        Assert.True(user.MustChangePassword);
        Assert.True(user.EmailConfirmed);
        Assert.False(user.IsAdmin);
        using (var db = app.Db()) Assert.Contains(db.AuditEvents, e => e.Action == "admin.user_created" && e.TargetId == user.Id.ToString());
        Assert.DoesNotContain(made.Password, string.Join("|", app.Db().AuditEvents.Select(e => e.Details)));

        await using var denied = app.Scope(app.UserId("bob"));
        await Assert.ThrowsAsync<ForbiddenException>(() => denied.Get<AdminUserService>().CreateUserAsync(AccountTestApp.NewName("x"), "X", "x@example.com", false));
        await using var reserved = app.Scope(app.UserId("alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reserved.Get<AdminUserService>().CreateUserAsync(AiBot.UserName, "Bot", "bot@example.com", false));
    }

    [Fact]
    public async Task An_invitation_lets_one_person_register_once()
    {
        string token;
        await using (var s = app.Scope(app.UserId("alice"))) (_, token) = await s.Get<RegistrationPolicy>().CreateInviteAsync(null, 7);
        var url = "/account/register?invite=" + token;
        Assert.Contains("reg-password", await app.Browser().GetStringAsync(url));

        var name = AccountTestApp.NewName("inv");
        var (ok, _) = await RegisterAsync(app.Browser(), name, url: url);
        Assert.True(AccountTestApp.IsRedirect(ok));
        Assert.True(Exists(name));

        // Single use: the link is dead now, on the page and on a post.
        Assert.Contains("invalid, expired or already used", await app.Browser().GetStringAsync(url));
        var again = AccountTestApp.NewName("inv2");
        await RegisterAsync(app.Browser(), again, url: url + "&x=1");
        Assert.False(Exists(again));
    }

    [Fact]
    public async Task Invitations_can_be_tied_to_an_address_and_revoked()
    {
        string tied, revoked;
        int revokedId;
        await using (var s = app.Scope(app.UserId("alice")))
        {
            var policy = s.Get<RegistrationPolicy>();
            (_, tied) = await policy.CreateInviteAsync("only-me@example.com", 1);
            var (invite, token) = await policy.CreateInviteAsync(null, 1);
            (revokedId, revoked) = (invite.Id, token);
            await policy.RevokeInviteAsync(revokedId);
        }
        var wrong = AccountTestApp.NewName("wrong");
        await RegisterAsync(app.Browser(), wrong, "someone-else@example.com", "/account/register?invite=" + tied);
        Assert.False(Exists(wrong));
        var right = AccountTestApp.NewName("right");
        Assert.True(AccountTestApp.IsRedirect((await RegisterAsync(app.Browser(), right, "only-me@example.com", "/account/register?invite=" + tied)).Response));
        Assert.True(Exists(right));

        var late = AccountTestApp.NewName("late");
        await RegisterAsync(app.Browser(), late, url: "/account/register?invite=" + revoked);
        Assert.False(Exists(late));
        using var db = app.Db();
        Assert.Contains(db.AuditEvents, e => e.Action == "admin.invite_revoked");
    }

    [Fact]
    public async Task Only_admins_manage_registration_and_the_database_value_beats_the_configuration()
    {
        await using (var s = app.Scope(app.UserId("bob")))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<RegistrationPolicy>().SetAsync(RegistrationMode.Open, []));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<RegistrationPolicy>().CreateInviteAsync(null, 1));
        }
        Assert.Equal(RegistrationMode.Closed, (await State()).Mode); // configuration: Registration:Mode=Closed

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<RegistrationPolicy>().SetAsync(RegistrationMode.Open, ["Example.com", "@acme.test"]);
        var state = await State();
        Assert.Equal(RegistrationMode.Open, state.Mode); // saved by an admin: wins, no restart
        Assert.Equal(["example.com", "acme.test"], state.AllowedDomains);

        var inside = AccountTestApp.NewName("dom");
        Assert.True(AccountTestApp.IsRedirect((await RegisterAsync(app.Browser(), inside, inside + "@example.com")).Response));
        var outside = AccountTestApp.NewName("out");
        var (_, body) = await RegisterAsync(app.Browser(), outside, outside + "@elsewhere.org");
        Assert.False(Exists(outside));
        Assert.Contains("Only email addresses at example.com, acme.test", body);
        using (var db = app.Db()) Assert.Contains(db.AuditEvents, e => e.Action == "admin.registration_changed");

        await using (var s = app.Scope(app.UserId("alice")))
            await s.Get<RegistrationPolicy>().SetAsync(RegistrationMode.Closed, []);
        Assert.Contains("Registration is closed", await app.Browser().GetStringAsync("/account/register"));
    }

    private async Task<RegistrationState> State()
    {
        await using var s = app.Scope();
        return await s.Get<RegistrationPolicy>().GetStateAsync();
    }
}

public class LockedRegistrationTests(LockedRegistrationApp app) : IClassFixture<LockedRegistrationApp>
{
    [Fact]
    public async Task Configuration_can_fix_the_setting_so_the_admin_page_cannot_change_it()
    {
        await using var s = app.Scope(app.UserId("alice"));
        var policy = s.Get<RegistrationPolicy>();
        var state = await policy.GetStateAsync();
        Assert.True(state.LockedByConfig);
        Assert.Equal(RegistrationMode.Closed, state.Mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetAsync(RegistrationMode.Open, []));
    }
}

/// <summary>Closed registration also stops SSO from creating accounts, but not existing linked users.</summary>
public class ClosedSsoTests(ClosedSsoApp app) : IClassFixture<ClosedSsoApp>
{
    [Fact]
    public async Task SSO_does_not_create_accounts_while_registration_is_closed()
    {
        var email = $"sso{Guid.NewGuid():N}"[..12] + "@example.org";
        var location = await app.SsoAsync(app.Browser(), "/account/sso/mock", new ByteBench.Tests.Sso.MockUser("sub-" + Guid.NewGuid().ToString("N")[..8], email));
        Assert.Contains("sso_error=registration_closed", location);
        using var db = app.Db();
        Assert.False(db.Users.Any(u => u.Email == email));

        // Existing accounts are unaffected: link one by hand and sign in again.
        var existing = await app.CreateUserAsync(AccountTestApp.NewName("ssoex"));
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            await users.AddLoginAsync((await users.FindByIdAsync(existing.Id.ToString()))!, new UserLoginInfo("mock", "known-sub", "Mock"));
        }
        var ok = await app.SsoAsync(app.Browser(), "/account/sso/mock", new ByteBench.Tests.Sso.MockUser("known-sub", existing.Email!));
        Assert.DoesNotContain("sso_error", ok);
    }
}

public sealed class ClosedSsoApp : ByteBench.Tests.Sso.SsoTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Registration:Mode"] = "Closed";
    }
}

/// <summary>The admin user-management additions: reset email, temporary password, permissions, system accounts.</summary>
public class AdminAccountActionTests(DefaultAccountApp app) : IClassFixture<DefaultAccountApp>
{
    [Fact]
    public async Task Send_reset_email_emails_the_link_and_is_audited()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("send"));
        await using (var s = app.Scope(app.UserId("alice"))) await s.Get<AdminUserService>().SendPasswordResetAsync(user.Id);
        var mail = Assert.Single(app.Mail.To(user.Email!));
        Assert.Contains("administrator", mail.Text);
        Assert.Contains("/account/reset-password?uid=" + user.Id, CapturingMailer.LinkOf(mail));
        using var db = app.Db();
        Assert.Contains(db.AuditEvents, e => e.Action == "admin.user_reset_email_sent" && e.TargetId == user.Id.ToString());

        // The link works like the self-service one.
        var token = System.Web.HttpUtility.ParseQueryString(CapturingMailer.LinkOf(mail).Split('?')[1])["token"]!;
        await using var s2 = app.Scope();
        Assert.Equal(ResetOutcome.Done, (await s2.Get<PasswordResetService>().ResetAsync(user.Id, token, "Admin-sent-pass1", "1.2.3.4")).Outcome);
    }

    [Fact]
    public async Task Send_reset_email_refuses_when_email_is_off_or_there_is_no_password()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("off"));
        app.Mail.Enabled = false;
        try
        {
            await using var s = app.Scope(app.UserId("alice"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.Get<AdminUserService>().SendPasswordResetAsync(user.Id));
            Assert.Contains("Email is switched off", ex.Message);
        }
        finally { app.Mail.Enabled = true; }
        var sso = await app.CreateUserWithoutPasswordAsync(AccountTestApp.NewName("nopw"));
        await using var s2 = app.Scope(app.UserId("alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.Get<AdminUserService>().SendPasswordResetAsync(sso.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s2.Get<AdminUserService>().SetTemporaryPasswordAsync(sso.Id));
    }

    [Fact]
    public async Task A_temporary_password_is_shown_once_signs_sessions_out_and_forces_a_change()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("temp"));
        var stamp = app.User(user.UserName!).SecurityStamp;
        TemporaryPassword temp;
        await using (var s = app.Scope(app.UserId("alice"))) temp = await s.Get<AdminUserService>().SetTemporaryPasswordAsync(user.Id);
        Assert.Equal(user.UserName, temp.UserName);
        Assert.True(temp.Password.Length >= 12);
        var after = app.User(user.UserName!);
        Assert.True(after.MustChangePassword);
        Assert.NotEqual(stamp, after.SecurityStamp);
        using (var db = app.Db())
        {
            Assert.Contains(db.AuditEvents, e => e.Action == "admin.user_temp_password" && e.TargetId == user.Id.ToString());
            Assert.DoesNotContain(temp.Password, string.Join("|", db.AuditEvents.Select(e => e.Details)));
        }
        Assert.Contains(app.Mail.To(user.Email!), m => m.Text.Contains("temporary password"));

        // The old password is dead; the temporary one works but everything redirects to the change page.
        Assert.Contains("Invalid username/email or password", (await app.LoginAsync(app.Browser(), user.UserName!, SeedData.DemoPassword)).Body);
        var browser = app.Browser();
        Assert.True(AccountTestApp.IsRedirect((await app.LoginAsync(browser, user.UserName!, temp.Password)).Response));
        var home = await browser.GetAsync("/explore");
        Assert.True(AccountTestApp.IsRedirect(home));
        Assert.Equal("/account/change-password", home.Headers.Location!.ToString());
        Assert.True(AccountTestApp.IsRedirect(await browser.GetAsync("/account/profile")));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/change-password")).StatusCode);

        // Choosing a new password lifts the restriction.
        var (done, _) = await AccountTestApp.PostFormAsync(browser, "/account/change-password", "change-password",
            ("Input.CurrentPassword", temp.Password), ("Input.NewPassword", "Chosen-by-user-1"), ("Input.ConfirmPassword", "Chosen-by-user-1"));
        Assert.True(AccountTestApp.IsRedirect(done));
        Assert.False(app.User(user.UserName!).MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/explore")).StatusCode);
    }

    [Fact]
    public async Task Admin_actions_need_an_admin_and_never_touch_system_accounts_or_yourself()
    {
        var user = await app.CreateUserAsync(AccountTestApp.NewName("perm"));
        await using (var s = app.Scope(app.UserId("bob")))
        {
            var admin = s.Get<AdminUserService>();
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.SendPasswordResetAsync(user.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.SetTemporaryPasswordAsync(user.Id));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.MarkEmailConfirmedAsync(user.Id));
        }
        Assert.False(app.User(user.UserName!).MustChangePassword);

        await using var a = app.Scope(app.UserId("alice"));
        var svc = a.Get<AdminUserService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetTemporaryPasswordAsync(app.UserId("alice"))); // use the profile page for your own password
        foreach (var botName in new[] { AiBot.UserName, ByteBench.Services.Automation.AutomationBot.UserName, ByteBench.Services.ServiceDesk.ServiceDeskBot.UserName })
        {
            int id;
            await using (var d = app.Db())
            {
                id = botName switch
                {
                    AiBot.UserName => await AiBot.EnsureAsync(d),
                    ByteBench.Services.Automation.AutomationBot.UserName => await ByteBench.Services.Automation.AutomationBot.EnsureAsync(d),
                    _ => await ByteBench.Services.ServiceDesk.ServiceDeskBot.EnsureAsync(d),
                };
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SendPasswordResetAsync(id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetTemporaryPasswordAsync(id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MarkEmailConfirmedAsync(id));
            Assert.True(SystemAccounts.IsSystem(botName));
        }
    }

    [Fact]
    public async Task The_last_admin_protection_still_holds()
    {
        await using var s = app.Scope(app.UserId("alice"));
        var admin = s.Get<AdminUserService>();
        var page = await admin.SearchAsync("alice", 1, 10);
        Assert.True(page.Users.Single(u => u.UserName == "alice").HasPassword);
        // Revoking every other admin is fine; revoking the last one is refused.
        foreach (var u in (await admin.SearchAsync(null, 1, 200)).Users.Where(u => u.IsAdmin && u.UserName != "alice"))
            await admin.SetAdminAsync(u.Id, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.SetAdminAsync(app.UserId("alice"), false));
    }

    [Fact]
    public void Generated_passwords_satisfy_the_password_policy()
    {
        for (var i = 0; i < 50; i++)
        {
            var p = AdminUserService.GeneratePassword();
            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
        }
    }
}
