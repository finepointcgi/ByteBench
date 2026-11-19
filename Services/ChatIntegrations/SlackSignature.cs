using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.ChatIntegrations;

public enum SlackSignatureResult { Valid, NotConfigured, Missing, Expired, Invalid, Replayed }

/// <summary>
/// Verifies Slack's request signing (v0): <c>X-Slack-Signature = "v0=" + hex(HMAC-SHA256(signingSecret, "v0:" + timestamp + ":" + rawBody))</c>.
/// Rejects timestamps outside <see cref="SlackOptions.TimestampToleranceSeconds"/> (either direction), compares in
/// constant time, and remembers accepted signatures for the tolerance window so a captured request can't be replayed.
/// Singleton.
/// </summary>
public sealed class SlackSignature(IOptionsMonitor<SlackOptions> options, TimeProvider clock)
{
    public const string SignatureHeader = "X-Slack-Signature";
    public const string TimestampHeader = "X-Slack-Request-Timestamp";

    private readonly ConcurrentDictionary<string, long> seen = new(StringComparer.Ordinal);
    private long lastPrune;

    public static string Compute(string secret, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"v0:{timestamp}:");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data, prefix.Length);
        return "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));
    }

    public SlackSignatureResult Verify(string? timestamp, string? signature, byte[] body)
    {
        var o = options.CurrentValue;
        if (!o.IsConfigured) return SlackSignatureResult.NotConfigured;
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return SlackSignatureResult.Missing;
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return SlackSignatureResult.Invalid;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var tolerance = Math.Max(30, o.TimestampToleranceSeconds);
        if (Math.Abs(now - ts) > tolerance) return SlackSignatureResult.Expired;

        var expected = Encoding.ASCII.GetBytes(Compute(o.SigningSecret!, timestamp, body));
        var actual = Encoding.ASCII.GetBytes(signature.Trim());
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return SlackSignatureResult.Invalid;

        Prune(now, tolerance);
        if (!seen.TryAdd(signature.Trim(), ts)) return SlackSignatureResult.Replayed;
        return SlackSignatureResult.Valid;
    }

    private void Prune(long now, int tolerance)
    {
        if (now - Interlocked.Read(ref lastPrune) < 30) return;
        Interlocked.Exchange(ref lastPrune, now);
        foreach (var (key, ts) in seen)
            if (Math.Abs(now - ts) > tolerance) seen.TryRemove(key, out _);
    }
}
