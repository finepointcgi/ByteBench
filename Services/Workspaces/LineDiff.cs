namespace ByteBench.Services.Workspaces;

/// <summary>One run of changed lines between two texts: <see cref="Index"/> is what accept/reject decisions refer to.</summary>
public sealed record DiffHunk(int Index, int OldStart, int NewStart, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines,
    IReadOnlyList<string> ContextBefore, IReadOnlyList<string> ContextAfter);

/// <summary>
/// A line diff of two texts split into hunks (each maximal run of changed lines is one hunk), and the inverse: building a text from the
/// original with only some hunks applied. Lines keep their own line ending, so <c>Apply(all hunks)</c> reproduces the new text exactly and
/// <c>Apply(no hunks)</c> the original. Very large inputs fall back to one hunk covering everything between the common prefix and suffix.
/// </summary>
public sealed class LineDiff
{
    public const int Context = 3;
    private const long MaxCells = 4_000_000;

    private readonly List<(bool Changed, string[] Old, string[] New)> parts = [];

    public IReadOnlyList<DiffHunk> Hunks { get; private set; } = [];

    public static LineDiff Compute(string? original, string? updated)
    {
        var a = Split(original ?? "");
        var b = Split(updated ?? "");
        var d = new LineDiff();
        var pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        var suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[^(suf + 1)] == b[^(suf + 1)]) suf++;
        var am = a[pre..^suf];
        var bm = b[pre..^suf];

        if (pre > 0) d.parts.Add((false, a[..pre], a[..pre]));
        foreach (var part in Middle(am, bm)) d.parts.Add(part);
        if (suf > 0) d.parts.Add((false, a[^suf..], a[^suf..]));
        d.BuildHunks();
        return d;
    }

    /// <summary>Lines of <paramref name="text"/> with their '\n' kept (a final line without one is kept as it is). Empty text has no lines.</summary>
    public static string[] Split(string text)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return lines.ToArray();
    }

    /// <summary>The original with the hunks whose index is in <paramref name="accepted"/> applied and every other hunk left out.</summary>
    public string Apply(ISet<int> accepted)
    {
        var sb = new System.Text.StringBuilder();
        var h = 0;
        foreach (var (changed, old, @new) in parts)
        {
            if (!changed) { foreach (var l in old) sb.Append(l); continue; }
            foreach (var l in accepted.Contains(h) ? @new : old) sb.Append(l);
            h++;
        }
        return sb.ToString();
    }

    public int AddedLines => Hunks.Sum(h => h.NewLines.Count);
    public int RemovedLines => Hunks.Sum(h => h.OldLines.Count);

    private void BuildHunks()
    {
        var hunks = new List<DiffHunk>();
        int oldLine = 1, newLine = 1;
        for (var i = 0; i < parts.Count; i++)
        {
            var (changed, old, @new) = parts[i];
            if (changed)
            {
                var before = i > 0 && !parts[i - 1].Changed ? parts[i - 1].Old.TakeLast(Context).ToArray() : [];
                var after = i + 1 < parts.Count && !parts[i + 1].Changed ? parts[i + 1].Old.Take(Context).ToArray() : [];
                hunks.Add(new DiffHunk(hunks.Count, oldLine, newLine, old, @new, before, after));
            }
            oldLine += old.Length;
            newLine += @new.Length;
        }
        Hunks = hunks;
    }

    private static IEnumerable<(bool, string[], string[])> Middle(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) yield break;
        if (a.Length == 0 || b.Length == 0 || (long)a.Length * b.Length > MaxCells)
        {
            yield return (true, a, b);
            yield break;
        }
        // Longest common subsequence table, then walk it forwards collecting equal and changed runs.
        var n = a.Length;
        var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var equal = new List<string>();
        var del = new List<string>();
        var ins = new List<string>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x] == b[y])
            {
                if (del.Count > 0 || ins.Count > 0) { yield return (true, del.ToArray(), ins.ToArray()); del.Clear(); ins.Clear(); }
                equal.Add(a[x]);
                x++; y++;
                continue;
            }
            if (equal.Count > 0) { var e = equal.ToArray(); yield return (false, e, e); equal.Clear(); }
            if (y >= m || (x < n && lcs[x + 1, y] >= lcs[x, y + 1])) del.Add(a[x++]);
            else ins.Add(b[y++]);
        }
        if (equal.Count > 0) { var e = equal.ToArray(); yield return (false, e, e); }
        if (del.Count > 0 || ins.Count > 0) yield return (true, del.ToArray(), ins.ToArray());
    }
}
