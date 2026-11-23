using System.Diagnostics;
using System.Globalization;
using System.Text;
using ByteBench.Services.Git;

namespace ByteBench.Services.Insights;

/// <summary>
/// Computes <see cref="InsightsData"/> from a bare repository. History comes from <c>git log --numstat</c>, streamed and
/// aggregated line by line (never held in memory), run without a shell (<c>ArgumentList</c>), against a commit sha we
/// resolved ourselves, with a commit limit and a timeout that kills the process. Languages come from the tree at the head.
/// </summary>
public static class InsightsComputer
{
    private const char CommitMarker = '\u0001';
    private const char FieldSeparator = '\u001f';

    public sealed record Result(InsightsData Data, int Commits, bool Truncated);

    public static async Task<Result> ComputeAsync(string gitPath, string repoPath, string headSha, InsightsOptions options, CancellationToken ct)
    {
        var data = new InsightsData();
        var (commits, truncated) = await ReadHistoryAsync(gitPath, repoPath, headSha, options, data, ct);
        data.Languages = Languages(repoPath, headSha, options.MaxFiles);
        return new Result(data, commits, truncated);
    }

    private static async Task<(int Commits, bool Truncated)> ReadHistoryAsync(string gitPath, string repoPath, string headSha, InsightsOptions options,
        InsightsData data, CancellationToken ct)
    {
        var max = Math.Max(1, options.MaxCommits);
        var psi = new ProcessStartInfo(gitPath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repoPath,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        foreach (var a in new[]
                 {
                     "--git-dir=" + repoPath, "log", "--no-merges", "--no-renames", "--numstat", "-n", (max + 1).ToString(CultureInfo.InvariantCulture),
                     $"--format={CommitMarker}%H{FieldSeparator}%aN{FieldSeparator}%aE{FieldSeparator}%at", headSha, "--",
                 })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        await using var kill = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
        });
        var stderr = DrainAsync(process.StandardError);

        var contributors = new Dictionary<string, ContributorData>(StringComparer.Ordinal);
        var nameCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var count = 0;
        var truncated = false;
        ContributorData? current = null;
        string? day = null;
        var add = 0; var del = 0;
        DateTime? first = null, last = null;

        void Flush()
        {
            if (current == null || day == null) return;
            if (!data.Days.TryGetValue(day, out var d)) data.Days[day] = d = new int[3];
            d[0]++; d[1] += add; d[2] += del;
            if (!current.Days.TryGetValue(day, out var c)) current.Days[day] = c = new int[3];
            c[0]++; c[1] += add; c[2] += del;
        }

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token)) != null)
            {
                if (line.Length > 0 && line[0] == CommitMarker)
                {
                    Flush();
                    current = null; day = null; add = del = 0;
                    if (count >= max) { truncated = true; break; }
                    var f = line[1..].Split(FieldSeparator);
                    if (f.Length < 4 || !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)) continue;
                    var when = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                    count++;
                    day = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    data.Hours[when.Hour]++;
                    data.Weekdays[(int)when.DayOfWeek]++;
                    if (first == null || when < first) first = when;
                    if (last == null || when > last) last = when;
                    var email = f[2].Trim().ToLowerInvariant();
                    var name = f[1].Trim();
                    var key = email.Length > 0 ? "e:" + email : "n:" + name.ToLowerInvariant();
                    if (!contributors.TryGetValue(key, out current)) contributors[key] = current = new ContributorData { Email = email, Name = name };
                    var names = nameCounts.TryGetValue(key, out var nc) ? nc : nameCounts[key] = new(StringComparer.Ordinal);
                    names[name] = names.GetValueOrDefault(name) + 1;
                    continue;
                }
                if (current == null || line.Length == 0) continue;
                var tab1 = line.IndexOf('\t');
                var tab2 = tab1 < 0 ? -1 : line.IndexOf('\t', tab1 + 1);
                if (tab2 < 0) continue;
                // "-\t-\tpath" is a binary file; excluded paths (lockfiles, vendored, generated) don't count either.
                if (!int.TryParse(line.AsSpan(0, tab1), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                    || !int.TryParse(line.AsSpan(tab1 + 1, tab2 - tab1 - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var d)) continue;
                if (LanguageTable.IsExcluded(line[(tab2 + 1)..])) continue;
                add += a; del += d;
            }
            Flush();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            truncated = true; // the time limit: keep what was read
            Flush();
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        }
        ct.ThrowIfCancellationRequested();
        await process.WaitForExitAsync(CancellationToken.None);
        var err = await stderr;
        if (process.ExitCode != 0 && count == 0 && !truncated)
            throw new InvalidOperationException("git log failed: " + (err.Length > 300 ? err[..300] : err).Trim());

        // Merge entries that share a name (the same person with several e-mail addresses), then pick each one's most used name.
        var merged = new Dictionary<string, ContributorData>(StringComparer.Ordinal);
        foreach (var (key, c) in contributors.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            c.Name = nameCounts[key].OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).First().Key;
            var nameKey = c.Name.Trim().ToLowerInvariant();
            if (nameKey.Length > 0 && merged.TryGetValue(nameKey, out var into))
            {
                foreach (var (dayKey, v) in c.Days)
                {
                    if (!into.Days.TryGetValue(dayKey, out var t)) into.Days[dayKey] = t = new int[3];
                    for (var i = 0; i < 3; i++) t[i] += v[i];
                }
                if (into.Email.Length == 0) into.Email = c.Email;
            }
            else merged[nameKey.Length > 0 ? nameKey : "k:" + key] = c;
        }
        data.Contributors = merged.Values.OrderByDescending(c => c.Days.Values.Sum(v => v[0])).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        data.FirstCommit = first?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        data.LastCommit = last?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (count, truncated);
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0) if (sb.Length < 8192) sb.Append(buffer, 0, n);
        return sb.ToString();
    }

    private static List<LanguageStat> Languages(string repoPath, string headSha, int maxFiles)
    {
        var files = GitOps.ListFiles(repoPath, headSha, Math.Max(1, maxFiles)) ?? [];
        var byLanguage = new Dictionary<string, (string Color, int Files, long Bytes)>(StringComparer.Ordinal);
        foreach (var (path, size) in files)
        {
            if (size <= 0 || LanguageTable.IsExcluded(path) || LanguageTable.Of(path) is not { } lang) continue;
            var cur = byLanguage.TryGetValue(lang.Name, out var existing) ? existing : (Color: lang.Color, Files: 0, Bytes: 0L);
            byLanguage[lang.Name] = (lang.Color, cur.Files + 1, cur.Bytes + size);
        }
        var total = byLanguage.Values.Sum(v => v.Bytes);
        return byLanguage.OrderByDescending(v => v.Value.Bytes).ThenBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => new LanguageStat(v.Key, v.Value.Color, v.Value.Files, v.Value.Bytes, total == 0 ? 0 : Math.Round(100.0 * v.Value.Bytes / total, 1)))
            .ToList();
    }
}
