using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Sso.Ldap;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests.Sso;

/// <summary>The app with LDAP on, talking to a <see cref="FakeLdapClient"/>.</summary>
public class LdapTestApp : TestApp
{
    public const string Base = "ou=people,dc=example,dc=org";
    public const string AdminsDn = "cn=gb-admins,ou=groups,dc=example,dc=org";
    public const string DevsDn = "cn=devs,ou=groups,dc=example,dc=org";
    public const string TeamGroup = "ldap-team";
    public FakeLdapClient Directory { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> s)
    {
        const string p = "Authentication:Ldap:";
        s[p + "Enabled"] = "true";
        s[p + "DisplayName"] = "Example LDAP";
        s[p + "Server"] = "fake.invalid";
        s[p + "Security"] = "None";
        s[p + "BindDn"] = "cn=svc,dc=example,dc=org";
        s[p + "UserSearchBase"] = Base;
        s[p + "UserFilter"] = "(&(objectClass=person)(uid={0}))";
        s[p + "Attributes:UserName"] = "uid";
        s[p + "Attributes:Email"] = "mail";
        s[p + "Attributes:DisplayName"] = "displayName";
        s[p + "Attributes:MemberOf"] = "memberOf";
        s[p + "AdminGroupDn"] = AdminsDn;
        s[p + "GroupMappings:0:GroupDn"] = "CN=devs, OU=groups, DC=example, DC=org"; // spacing and case differ on purpose
        s[p + "GroupMappings:0:Group"] = TeamGroup;
        s[p + "GroupMappings:0:Role"] = "Developer";
        s[p + "AllowGitPassword"] = "true";
        s[p + "Sync:DeactivateMissingUsers"] = "true";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<ILdapClient>(Directory));
    }

    public LdapOptions Options => Services.GetRequiredService<IOptionsMonitor<LdapOptions>>().CurrentValue;
}

public class LdapFilterTests
{
    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("*", @"\2a")]
    [InlineData("*)(uid=*", @"\2a\29\28uid=\2a")]
    [InlineData(@"a\b", @"a\5cb")]
    [InlineData("a\0b", @"a\00b")]
    [InlineData("José", @"Jos\c3\a9")]
    [InlineData("x)(|(objectClass=*", @"x\29\28|\28objectClass=\2a")]
    public void Escapes_filter_values_per_rfc4515(string input, string expected) => Assert.Equal(expected, LdapFilter.Escape(input));

    [Fact]
    public void Format_only_substitutes_the_escaped_value()
    {
        Assert.Equal(@"(&(objectClass=person)(uid=\2a\29\28uid=\2a))", LdapFilter.Format("(&(objectClass=person)(uid={0}))", "*)(uid=*"));
        Assert.Equal("(cn={1})", LdapFilter.Format("(cn={1})", "x")); // no other format syntax
    }

    [Fact]
    public void Dns_compare_without_case_or_spacing() =>
        Assert.Equal(LdapAuthService.NormalizeDn("cn=devs,ou=groups,dc=example,dc=org"), LdapAuthService.NormalizeDn("CN = devs , OU=groups,DC=example, dc=org"));
}

/// <summary>LDAP sign-in, group sync, git auth and the directory sync, against the fake directory.</summary>
public class LdapTests(LdapTestApp app) : IClassFixture<LdapTestApp>
{
    private static string NewUid() => "l" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string login, string password)
    {
        var page = await client.GetStringAsync("/account/login");
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = SsoTestApp.Token(page),
            ["Input.UserNameOrEmail"] = login,
            ["Input.Password"] = password,
        }));
    }

    private HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<User?> FindAsync(string userName)
    {
        await using var s = app.Scope();
        return await s.Get<UserManager<User>>().FindByNameAsync(userName);
    }

    private async Task<Group> TeamAsync()
    {
        using (var db = app.Db())
            if (db.Groups.FirstOrDefault(g => g.FullPath == LdapTestApp.TeamGroup) is { } g) return g;
        await using var s = app.Scope(app.UserId("alice"));
        return await s.Get<GroupService>().CreateAsync("LDAP team", LdapTestApp.TeamGroup, null, ProjectVisibility.Private);
    }

    [Fact]
    public async Task Sign_in_creates_the_account_and_syncs_admin_and_groups()
    {
        var team = await TeamAsync();
        var uid = NewUid();
        app.Directory.AddUser(uid, "dir-pass-1", uid + "@corp.example", "Lee Directory", LdapTestApp.DevsDn, LdapTestApp.AdminsDn);

        var browser = Browser();
        var res = await PostLoginAsync(browser, uid, "dir-pass-1");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/profile")).StatusCode);

        var user = (await FindAsync(uid))!;
        Assert.Equal("Lee Directory", user.DisplayName);
        Assert.Null(user.PasswordHash);
        Assert.True(user.IsAdmin);
        Assert.Equal("ldap", user.AdminGrantedBy);
        using (var db = app.Db())
        {
            Assert.Equal(ProjectRole.Developer, db.GroupMembers.Single(m => m.GroupId == team.Id && m.UserId == user.Id).Role);
            Assert.True(db.UserLogins.Any(l => l.UserId == user.Id && l.LoginProvider == "ldap" && l.ProviderKey == uid));
            Assert.True(db.AuditEvents.Any(e => e.Action == "auth.sign_in" && e.ActorId == user.Id && e.Details!.Contains("ldap")));
        }

        // Leaving the groups in the directory removes them on the next sign-in.
        app.Directory.SetGroups(uid);
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(Browser(), uid, "dir-pass-1")).StatusCode);
        Assert.False((await FindAsync(uid))!.IsAdmin);
        using (var db = app.Db()) Assert.False(db.GroupMembers.Any(m => m.GroupId == team.Id && m.UserId == user.Id));

        // The profile says the password lives in the directory.
        var browser2 = Browser();
        await PostLoginAsync(browser2, uid, "dir-pass-1");
        var profileHtml = await browser2.GetStringAsync("/account/profile");
        var at = profileHtml.IndexOf("managed by");
        Assert.True(at > 0);
        Assert.Contains("Example LDAP", profileHtml[at..(at + 80)]);
    }

    [Fact]
    public async Task Wrong_and_empty_passwords_fail_and_count_toward_lockout()
    {
        var uid = NewUid();
        app.Directory.AddUser(uid, "right-pass", uid + "@corp.example");
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(Browser(), uid, "right-pass")).StatusCode);

        var bad = await PostLoginAsync(Browser(), uid, "wrong-pass");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Invalid username/email or password.", await bad.Content.ReadAsStringAsync());
        Assert.Equal(1, (await FindAsync(uid))!.AccessFailedCount);

        // An empty password never reaches the directory (it would be an unauthenticated bind).
        await using (var s = app.Scope())
        {
            var binds = app.Directory.Binds.Count;
            var r = await s.Get<LdapAuthService>().AuthenticateAsync(uid, "");
            Assert.Equal(LdapLoginStatus.InvalidCredentials, r.Status);
            Assert.Equal(binds, app.Directory.Binds.Count);
        }

        for (var i = 0; i < 5; i++) await PostLoginAsync(Browser(), uid, "wrong-pass");
        var locked = await PostLoginAsync(Browser(), uid, "right-pass");
        Assert.Contains("locked or deactivated", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Filter_injection_does_not_match_other_entries()
    {
        var uid = NewUid();
        app.Directory.AddUser(uid, "inj-pass", uid + "@corp.example");
        await using var s = app.Scope();
        var ldap = s.Get<LdapAuthService>();
        foreach (var attack in new[] { "*", uid[..3] + "*", $"{uid})(objectClass=*", "*)(|(uid=*", $"{uid})(|(uid=*" })
        {
            Assert.Null(await ldap.FindEntryAsync(attack));
            Assert.Equal(LdapLoginStatus.InvalidCredentials, (await ldap.AuthenticateAsync(attack, "inj-pass")).Status);
        }
        Assert.Contains(app.Directory.Filters, f => f.Contains(@"\2a\29\28|\28uid=\2a"));
        Assert.NotNull(await ldap.FindEntryAsync(uid));
    }

    [Fact]
    public async Task Directory_logins_never_take_over_local_accounts()
    {
        // The directory has a "bob" too; bob is a local account.
        app.Directory.AddUser("bob", "dir-bob", "bob@corp.example");
        var res = await PostLoginAsync(Browser(), "ldap:bob", "dir-bob");
        Assert.Contains("already exists", await res.Content.ReadAsStringAsync());
        using (var db = app.Db()) Assert.False(db.UserLogins.Any(l => l.UserId == app.UserId("bob") && l.LoginProvider == "ldap"));

        // Without the prefix, bob's local password works as before (no LDAP).
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(Browser(), "bob", SeedData.DemoPassword)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, (await PostLoginAsync(Browser(), "bob", "dir-bob")).StatusCode);
        app.Directory.Remove("bob");
    }

    [Fact]
    public async Task Directory_down_is_reported_not_treated_as_bad_password()
    {
        var uid = NewUid();
        app.Directory.AddUser(uid, "pw-down", uid + "@corp.example");
        app.Directory.Down = true;
        try
        {
            var res = await PostLoginAsync(Browser(), uid, "pw-down");
            Assert.Contains("can&#x27;t be reached", await res.Content.ReadAsStringAsync());
        }
        finally { app.Directory.Down = false; }
    }

    [Fact]
    public async Task Git_with_an_ldap_password_only_when_allowed_and_without_2fa()
    {
        var uid = NewUid();
        app.Directory.AddUser(uid, "git-ldap-pass", uid + "@corp.example");
        await PostLoginAsync(Browser(), uid, "git-ldap-pass"); // creates the account
        var project = await app.CreateProjectAsync("alice", ProjectVisibility.Private, (uid, ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id);
        var url = $"/git/{project.Key}/{repo.Slug}.git/info/refs?service=git-upload-pack";

        async Task<HttpResponseMessage> Git(string secret)
        {
            var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{uid}:{secret}")));
            return await client.GetAsync(url);
        }

        Assert.Equal(HttpStatusCode.OK, (await Git("git-ldap-pass")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Git("nope")).StatusCode);

        // LDAP git passwords turned off: tokens only.
        app.Options.AllowGitPassword = false;
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Git("git-ldap-pass")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Git(await app.CreateTokenAsync(uid, TokenScopes.ReadRepository))).StatusCode);
        }
        finally { app.Options.AllowGitPassword = true; }

        // With ByteBench 2FA on, the LDAP password is refused with a message.
        await using (var s = app.Scope())
        {
            var users = s.Get<UserManager<User>>();
            var u = (await users.FindByNameAsync(uid))!;
            await users.ResetAuthenticatorKeyAsync(u);
            await users.SetTwoFactorEnabledAsync(u, true);
        }
        var twoFactor = await Git("git-ldap-pass");
        Assert.Equal(HttpStatusCode.Unauthorized, twoFactor.StatusCode);
        Assert.Contains("two-factor", await twoFactor.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sync_updates_and_deactivates_missing_users_and_stops_when_the_directory_is_down()
    {
        var stay = NewUid();
        var gone = NewUid();
        app.Directory.AddUser(stay, "p1", stay + "@corp.example", "Before");
        app.Directory.AddUser(gone, "p2", gone + "@corp.example");
        await PostLoginAsync(Browser(), stay, "p1");
        await PostLoginAsync(Browser(), gone, "p2");
        var token = await app.CreateTokenAsync(gone, TokenScopes.ReadRepository);

        // Down: the run fails and nobody is deactivated.
        app.Directory.Remove(gone);
        app.Directory.Down = true;
        var job = app.Services.GetRequiredService<LdapSyncJob>();
        DirectorySyncRun? run;
        try { run = await job.RunNowAsync("manual"); }
        finally { app.Directory.Down = false; }
        Assert.False(run!.Succeeded);
        Assert.Null((await FindAsync(gone))!.DeactivatedAt);

        app.Directory.Remove(stay);
        app.Directory.AddUser(stay, "p1", stay + "@corp.example", "After");
        run = await job.RunNowAsync("manual");
        Assert.True(run!.Succeeded, run.Error);
        Assert.True(run.UsersDeactivated >= 1);
        Assert.Equal("After", (await FindAsync(stay))!.DisplayName);
        var goneUser = (await FindAsync(gone))!;
        Assert.NotNull(goneUser.DeactivatedAt);
        using (var db = app.Db())
        {
            Assert.All(db.PersonalAccessTokens.Where(t => t.UserId == goneUser.Id), t => Assert.NotNull(t.RevokedAt));
            Assert.Equal(run.Id, db.DirectorySyncRuns.OrderByDescending(r => r.Id).First().Id);
        }
        _ = token;
        var res = await PostLoginAsync(Browser(), gone, "p2");
        Assert.Contains("Invalid username", await res.Content.ReadAsStringAsync()); // no longer in the directory
    }

    [Fact]
    public async Task Admin_page_shows_status_without_secrets()
    {
        var alice = await app.CookieClientAsync("alice");
        var html = await alice.GetStringAsync("/admin/authentication");
        Assert.Contains("LDAP / Active Directory", html);
        Assert.Contains("fake.invalid:389", html);
        Assert.DoesNotContain("BindPassword", html);
        await using var s = app.Scope();
        var (ok, message) = await s.Get<LdapAuthService>().TestConnectionAsync();
        Assert.True(ok, message);
    }
}
