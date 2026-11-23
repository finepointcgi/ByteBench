using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Accounts;
using ByteBench.Services.Email;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ByteBench.Tests.Infrastructure;

/// <summary>An in-memory mailbox standing in for the email queue: nothing is queued, sent or awaited on a background worker.</summary>
public sealed class CapturingMailer : IAccountMailer
{
    private readonly ConcurrentQueue<EmailMessage> sent = new();
    public bool Enabled { get; set; } = true;
    public bool IsEnabled => Enabled;
    public IReadOnlyList<EmailMessage> All => sent.ToList();
    public List<EmailMessage> To(string email) => sent.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool Send(EmailMessage message)
    {
        if (!Enabled) return false;
        sent.Enqueue(message);
        return true;
    }

    /// <summary>The path and query of the first link in the message's plain-text body.</summary>
    public static string LinkOf(EmailMessage m) => new Uri(Regex.Match(m.Text, @"https?://\S+").Value).PathAndQuery;
}

/// <summary>The app with the capturing mailer and no artificial response delay. Derived fixtures set the account-related options.</summary>
public class AccountTestApp : TestApp
{
    public CapturingMailer Mail => Services.GetRequiredService<CapturingMailer>();

    protected override void AddSettings(IDictionary<string, string?> s)
    {
        s["Account:ResponseFloorMs"] = "0";
        s["Account:MaxRequestsPerIp"] = "1000";
        s["Account:MaxRequestsPerAccount"] = "1000";
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<CapturingMailer>();
        services.RemoveAll<IAccountMailer>();
        services.AddSingleton<IAccountMailer>(sp => sp.GetRequiredService<CapturingMailer>());
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>GETs <paramref name="url"/> for its antiforgery token, then POSTs the static SSR form <paramref name="formName"/> back to it.</summary>
    public static async Task<(HttpResponseMessage Response, string Body)> PostFormAsync(HttpClient client, string url, string formName, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(url);
        var form = new Dictionary<string, string> { ["_handler"] = formName, ["__RequestVerificationToken"] = Token(page) };
        foreach (var (k, v) in fields) form[k] = v;
        var response = await client.PostAsync(url, new FormUrlEncodedContent(form));
        return (response, await response.Content.ReadAsStringAsync());
    }

    public Task<(HttpResponseMessage Response, string Body)> LoginAsync(HttpClient client, string login, string password) =>
        PostFormAsync(client, "/account/login", "login", ("Input.UserNameOrEmail", login), ("Input.Password", password));

    public async Task<User> CreateUserWithoutPasswordAsync(string userName)
    {
        await using var s = Scope();
        var user = new User { UserName = userName, Email = userName + "@example.com", DisplayName = userName, Color = "#123456", EmailConfirmed = true };
        var r = await s.Get<Microsoft.AspNetCore.Identity.UserManager<User>>().CreateAsync(user);
        if (!r.Succeeded) throw new InvalidOperationException(string.Join("; ", r.Errors.Select(e => e.Description)));
        return user;
    }

    public static string NewName(string prefix = "u") => prefix + Guid.NewGuid().ToString("N")[..8];

    public static bool IsRedirect(HttpResponseMessage r) => r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther;
}

public sealed class DefaultAccountApp : AccountTestApp;

/// <summary>Email confirmation required; email changes need confirming.</summary>
public sealed class ConfirmAccountApp : AccountTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Account:RequireEmailConfirmation"] = "true";
    }
}

/// <summary>Registration closed by configuration, but editable by admins.</summary>
public sealed class ClosedAccountApp : AccountTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Registration:Mode"] = "Closed";
    }
}

/// <summary>Registration closed and fixed by configuration.</summary>
public sealed class LockedRegistrationApp : AccountTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Registration:Mode"] = "Closed";
        s["Registration:LockedByConfig"] = "true";
    }
}

/// <summary>Tight request limits (5 per IP, 2 per account) and a 90 minute reset link.</summary>
public sealed class ThrottledAccountApp : AccountTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Account:MaxRequestsPerIp"] = "5";
        s["Account:MaxRequestsPerAccount"] = "2";
        s["Account:PasswordResetMinutes"] = "90";
    }
}

/// <summary>Keeps the production response-time floor (200 ms here) so timing can be asserted.</summary>
public sealed class FloorAccountApp : AccountTestApp
{
    protected override void AddSettings(IDictionary<string, string?> s)
    {
        base.AddSettings(s);
        s["Account:ResponseFloorMs"] = "200";
    }
}

/// <summary>Reset links that are already expired when they are used.</summary>
public sealed class ExpiredLinkApp : AccountTestApp
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.PostConfigure<PasswordResetTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromSeconds(-5));
    }
}
