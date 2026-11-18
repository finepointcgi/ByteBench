namespace GitBench.Services.Ci;

/// <summary>
/// Replaces CI secret values with <c>***</c> in step logs. Values shorter than <see cref="MinLength"/> characters are
/// not masked (masking "1" or "on" would garble every log). The runner applies <see cref="Mask"/> to the whole log
/// text every time it is stored, never to single output chunks, so a value that arrives split across reads or lines is
/// still masked once its last piece is in; the partial prefix that may be pending at one flush is also hidden by
/// <see cref="Mask"/>'s trailing-prefix check.
/// </summary>
public sealed class CiSecretMasker
{
    public const int MinLength = 4;
    public const string Replacement = "***";
    public static readonly CiSecretMasker Empty = new([]);

    private readonly string[] secrets;

    public CiSecretMasker(IEnumerable<string> values)
    {
        // Longest first, so a value that contains another one is masked whole.
        secrets = values.Where(v => v.Length >= MinLength).Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length).ToArray();
    }

    public bool IsEmpty => secrets.Length == 0;

    /// <summary>
    /// <paramref name="text"/> with every secret replaced by <c>***</c>. If the text ends with the beginning of a secret
    /// (at least <see cref="MinLength"/> characters of it: the rest may still be on its way), that tail is masked too.
    /// </summary>
    public string Mask(string text)
    {
        if (secrets.Length == 0 || text.Length == 0) return text;
        foreach (var s in secrets)
            if (text.Contains(s, StringComparison.Ordinal)) text = text.Replace(s, Replacement, StringComparison.Ordinal);
        foreach (var s in secrets)
        {
            for (var n = Math.Min(s.Length - 1, text.Length); n >= MinLength; n--)
            {
                if (text.AsSpan(text.Length - n).SequenceEqual(s.AsSpan(0, n)))
                    return text[..^n] + Replacement;
            }
        }
        return text;
    }
}
