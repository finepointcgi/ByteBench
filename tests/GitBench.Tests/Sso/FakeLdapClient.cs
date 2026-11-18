using System.Text;
using GitBench.Services.Sso.Ldap;

namespace GitBench.Tests.Sso;

/// <summary>
/// An in-memory directory for <see cref="ILdapClient"/>. Searches really evaluate the filter (a small RFC 4515 parser:
/// <c>&amp;</c>, <c>|</c>, <c>!</c>, equality, presence and substring with <c>\XX</c> escapes), so filter-injection
/// tests mean something.
/// </summary>
public sealed class FakeLdapClient : ILdapClient
{
    public sealed record Entry(string Dn, string Password, Dictionary<string, string[]> Attributes);

    private readonly List<Entry> entries = [];
    public List<string> Filters { get; } = [];
    public List<string> Binds { get; } = [];
    /// <summary>Simulates the directory being down.</summary>
    public bool Down { get; set; }

    public void Clear() { lock (entries) entries.Clear(); }

    public Entry AddUser(string uid, string password, string? mail, string? displayName = null, params string[] memberOf)
    {
        var e = new Entry($"uid={uid},ou=people,dc=example,dc=org", password, new(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = ["person", "inetOrgPerson"],
            ["uid"] = [uid],
            ["mail"] = mail == null ? [] : [mail],
            ["displayName"] = [displayName ?? uid],
            ["memberOf"] = memberOf,
        });
        lock (entries) entries.Add(e);
        return e;
    }

    public void Remove(string uid) { lock (entries) entries.RemoveAll(e => e.Attributes["uid"].Contains(uid)); }

    public void SetGroups(string uid, params string[] memberOf) { lock (entries) entries.First(e => e.Attributes["uid"].Contains(uid)).Attributes["memberOf"] = memberOf; }

    public Task<IReadOnlyList<LdapDirectoryEntry>> SearchAsync(string baseDn, string filter, string[] attributes, CancellationToken ct = default)
    {
        if (Down) throw new LdapUnavailableException("Could not connect to fake:389 (down).");
        lock (Filters) Filters.Add(filter);
        var parsed = Parse(filter);
        List<Entry> snapshot;
        lock (entries) snapshot = entries.ToList();
        IReadOnlyList<LdapDirectoryEntry> result = snapshot
            .Where(e => e.Dn.EndsWith(baseDn, StringComparison.OrdinalIgnoreCase) && parsed(e))
            .Select(e => new LdapDirectoryEntry(e.Dn, attributes.Where(e.Attributes.ContainsKey).ToDictionary(a => a, a => e.Attributes[a])))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> CheckPasswordAsync(string dn, string password, CancellationToken ct = default)
    {
        if (Down) throw new LdapUnavailableException("down");
        lock (Binds) Binds.Add(dn);
        Entry? e;
        lock (entries) e = entries.FirstOrDefault(x => x.Dn.Equals(dn, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(e != null && password.Length > 0 && e.Password == password);
    }

    // ---------- a minimal RFC 4515 filter evaluator ----------

    private static Func<Entry, bool> Parse(string filter)
    {
        var pos = 0;
        var f = ParseFilter(filter, ref pos);
        if (pos != filter.Length) throw new FormatException($"Trailing characters in filter: {filter}");
        return f;
    }

    private static Func<Entry, bool> ParseFilter(string s, ref int pos)
    {
        Expect(s, ref pos, '(');
        Func<Entry, bool> result;
        switch (s[pos])
        {
            case '&':
            case '|':
                var op = s[pos++];
                var parts = new List<Func<Entry, bool>>();
                while (s[pos] == '(') parts.Add(ParseFilter(s, ref pos));
                result = op == '&' ? e => parts.All(p => p(e)) : e => parts.Any(p => p(e));
                break;
            case '!':
                pos++;
                var inner = ParseFilter(s, ref pos);
                result = e => !inner(e);
                break;
            default:
                var eq = s.IndexOf('=', pos);
                var attr = s[pos..eq];
                var end = s.IndexOf(')', eq);
                var raw = s[(eq + 1)..end];
                pos = end;
                if (attr.IndexOfAny(['(', '*', '\\']) >= 0) throw new FormatException("Bad attribute in filter");
                if (raw == "*") result = e => e.Attributes.TryGetValue(attr, out var v) && v.Length > 0;
                else
                {
                    var pieces = raw.Split('*').Select(Unescape).ToArray(); // unescaped '*' are wildcards
                    result = e => e.Attributes.TryGetValue(attr, out var values) && values.Any(v => Matches(v, pieces));
                }
                break;
        }
        Expect(s, ref pos, ')');
        return result;
    }

    private static bool Matches(string value, string[] pieces)
    {
        if (pieces.Length == 1) return value.Equals(pieces[0], StringComparison.OrdinalIgnoreCase);
        var at = 0;
        if (!value.StartsWith(pieces[0], StringComparison.OrdinalIgnoreCase)) return false;
        at = pieces[0].Length;
        for (var i = 1; i < pieces.Length - 1; i++)
        {
            var idx = value.IndexOf(pieces[i], at, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            at = idx + pieces[i].Length;
        }
        return value.Length - at >= pieces[^1].Length && value.EndsWith(pieces[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static string Unescape(string s)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else if (s[i] is '(' or ')') throw new FormatException("Unescaped parenthesis in value");
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void Expect(string s, ref int pos, char c)
    {
        if (pos >= s.Length || s[pos] != c) throw new FormatException($"Expected '{c}' at {pos} in {s}");
        pos++;
    }
}
