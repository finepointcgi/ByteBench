using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Email;

/// <summary>Sends through an SMTP server with MailKit (<c>Email:Smtp:*</c>). One connection per message: volume is low.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Smtp.Host))
            throw new InvalidOperationException("Email:Smtp:Host is not set.");
        var security = o.Smtp.UseStartTls ? SecureSocketOptions.StartTls
            : o.Smtp.Port == 465 ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.None;

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Smtp.Host, o.Smtp.Port, security, ct);
        if (!string.IsNullOrEmpty(o.Smtp.User))
            await client.AuthenticateAsync(o.Smtp.User, o.Smtp.Password ?? "", ct);
        await client.SendAsync(new MailKit.SendRequest(MimeBuilder.Build(message, o.From)), ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// Writes each message as an <c>.eml</c> file into <c>Email:PickupDirectory</c> (default <c>./mail-drop</c>).
/// Nothing leaves the machine; open the files with any mail client. The default in Development.
/// </summary>
public sealed class PickupDirectoryEmailSender(IOptions<EmailOptions> options, IHostEnvironment env) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        var dir = Path.GetFullPath(o.PickupDirectory, env.ContentRootPath);
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.eml";
        var tmp = Path.Combine(dir, name + ".tmp");
        // Write then rename, so a mail client or test watching the folder never sees half a file.
        await MimeBuilder.Build(message, o.From).WriteToAsync(tmp, ct);
        File.Move(tmp, Path.Combine(dir, name));
    }
}
