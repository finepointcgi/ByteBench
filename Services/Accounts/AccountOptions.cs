namespace ByteBench.Services.Accounts;

/// <summary>
/// The <c>Account</c> configuration section: password reset, email confirmation and the throttles around them.
/// </summary>
public sealed class AccountOptions
{
    /// <summary>How long a password reset link works (minutes).</summary>
    public int PasswordResetMinutes { get; set; } = 60;

    /// <summary>
    /// When true, a password sign-in needs a confirmed email address, new registrations must confirm before signing in
    /// and a changed email address must be confirmed before it replaces the old one. Default false.
    /// </summary>
    public bool RequireEmailConfirmation { get; set; }

    /// <summary>How long an email confirmation link works (hours).</summary>
    public int EmailConfirmationHours { get; set; } = 48;

    /// <summary>Requests (reset, confirmation resend) one client IP may make per <see cref="ThrottleWindowMinutes"/>.</summary>
    public int MaxRequestsPerIp { get; set; } = 10;

    /// <summary>Emails of one kind one account may be sent per <see cref="ThrottleWindowMinutes"/> (extra requests still get the neutral answer).</summary>
    public int MaxRequestsPerAccount { get; set; } = 3;

    public int ThrottleWindowMinutes { get; set; } = 15;

    /// <summary>
    /// Requests for a reset or confirmation email take at least this long (milliseconds), so response time doesn't show whether
    /// the address belongs to an account. 0 turns it off.
    /// </summary>
    public int ResponseFloorMs { get; set; } = 400;

    public TimeSpan ThrottleWindow => TimeSpan.FromMinutes(Math.Max(1, ThrottleWindowMinutes));
}

/// <summary>
/// The <c>Registration</c> configuration section. Precedence: with <see cref="LockedByConfig"/> the configuration always
/// wins; otherwise a value an admin saved on /admin/users (stored in the database) wins; otherwise the configuration;
/// otherwise Open with no domain restriction.
/// </summary>
public sealed class RegistrationOptions
{
    /// <summary><c>Open</c> or <c>Closed</c>. The initial default (or, with <see cref="LockedByConfig"/>, the fixed value).</summary>
    public string? Mode { get; set; }

    /// <summary>Allowed email domains (e.g. <c>example.com</c>); empty allows any. Same precedence as <see cref="Mode"/>.</summary>
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>When true the admin page shows the settings read-only and the database value is ignored.</summary>
    public bool LockedByConfig { get; set; }

    /// <summary>Whether SSO and LDAP first-time account creation obeys the mode and the domain list. Default true.</summary>
    public bool ApplyToSso { get; set; } = true;
}
