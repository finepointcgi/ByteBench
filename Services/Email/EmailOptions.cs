namespace GitBench.Services.Email;

/// <summary>
/// The <c>Email</c> configuration section. Credentials never go in appsettings: set them with user-secrets or
/// environment variables (<c>Email__Smtp__Password</c>). See the README's "Email" section.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>Master switch. When false nothing is sent and pending notification emails are dropped.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>Pickup</c> (write .eml files to <see cref="PickupDirectory"/>) or <c>Smtp</c>. Default: Pickup in Development, Smtp otherwise.</summary>
    public string? Sender { get; set; }

    /// <summary>Sender address, e.g. <c>GitBench &lt;gitbench@example.com&gt;</c>.</summary>
    public string From { get; set; } = "GitBench <gitbench@localhost>";

    /// <summary>Where the pickup sender writes .eml files (relative to the content root).</summary>
    public string PickupDirectory { get; set; } = "mail-drop";

    /// <summary>
    /// 0 sends every notification email immediately. N &gt; 0 sends at most one email per user every N minutes:
    /// the first goes out at once, later ones are batched into a digest.
    /// </summary>
    public int DigestMinutes { get; set; }

    /// <summary>Absolute site URL for links in emails. Falls back to <c>PublicBaseUrl</c>, then the URL of the first request.</summary>
    public string? BaseUrl { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    public sealed class SmtpOptions
    {
        public string? Host { get; set; }
        public int Port { get; set; } = 587;
        public string? User { get; set; }
        public string? Password { get; set; }
        /// <summary>STARTTLS (port 587). False with port 465 uses implicit TLS; false on other ports sends in clear text (local relays only).</summary>
        public bool UseStartTls { get; set; } = true;
    }
}
