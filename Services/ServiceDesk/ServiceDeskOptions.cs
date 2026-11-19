namespace GitBench.Services.ServiceDesk;

/// <summary>
/// The <c>ServiceDesk</c> configuration section. Secrets (<see cref="WebhookSecret"/>, <c>Imap:Password</c>) never go in
/// appsettings: set them with user-secrets or environment variables (<c>ServiceDesk__WebhookSecret</c>).
/// </summary>
public sealed class ServiceDeskOptions
{
    /// <summary>
    /// The inbound mailbox, e.g. <c>desk@support.example.com</c>. Projects get <c>desk+&lt;key&gt;@support.example.com</c>
    /// (plus-addressing) and replies go to <c>desk+r-&lt;token&gt;@support.example.com</c>. Projects can also have a custom address.
    /// </summary>
    public string IncomingAddress { get; set; } = "desk@gitbench.localhost";

    /// <summary>Shared secret for <c>POST /service-desk/inbound</c> (raw MIME, HMAC-SHA256 signed). Empty: the endpoint is off.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>How far a webhook's <c>X-GitBench-Timestamp</c> may be from now (replay protection).</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;

    /// <summary>Largest raw message accepted (webhook body or IMAP message).</summary>
    public long MaxMessageBytes { get; set; } = 30L * 1024 * 1024;

    /// <summary>Per attachment; also capped by <c>Attachments:MaxBytes</c>.</summary>
    public long MaxAttachmentBytes { get; set; } = 10L * 1024 * 1024;

    public int MaxAttachmentsPerMessage { get; set; } = 10;

    /// <summary>File extensions never accepted as attachments (executables and scripts).</summary>
    public List<string> BlockedExtensions { get; set; } =
        [".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".msi", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".ps1", ".jar", ".lnk", ".hta", ".dll", ".cpl", ".reg"];

    /// <summary>Longest description/comment kept from one message; the rest is cut.</summary>
    public int MaxBodyChars { get; set; } = 20_000;

    /// <summary>Messages (new requests and replies) accepted per sender address per hour.</summary>
    public int MaxMessagesPerSenderPerHour { get; set; } = 20;

    /// <summary>Public form submissions (new requests and tracking-page replies) per client IP per hour.</summary>
    public int MaxFormSubmissionsPerHour { get; set; } = 10;

    /// <summary>A message with more links than this is treated as spam.</summary>
    public int MaxLinks { get; set; } = 25;

    /// <summary>Sender addresses or <c>@domain</c>s that are always refused.</summary>
    public List<string> BlockedSenders { get; set; } = [];

    public ImapOptions Imap { get; set; } = new();

    public sealed class ImapOptions
    {
        /// <summary>Off by default. When on, the mailbox is polled every <see cref="PollSeconds"/>.</summary>
        public bool Enabled { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; } = 993;
        /// <summary>Implicit TLS (993). False uses STARTTLS when offered.</summary>
        public bool UseSsl { get; set; } = true;
        public string? User { get; set; }
        public string? Password { get; set; }
        public string Folder { get; set; } = "INBOX";
        public int PollSeconds { get; set; } = 60;
        /// <summary>Delete processed messages instead of only marking them seen.</summary>
        public bool DeleteProcessed { get; set; }
    }
}
