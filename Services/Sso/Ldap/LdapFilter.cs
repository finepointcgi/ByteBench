using System.Text;

namespace GitBench.Services.Sso.Ldap;

/// <summary>Builds LDAP search filters from configured templates with user input escaped per RFC 4515.</summary>
public static class LdapFilter
{
    /// <summary>
    /// Escapes a value for use inside an LDAP filter (RFC 4515 §3): <c>*</c>, <c>(</c>, <c>)</c>, <c>\</c> and NUL become
    /// <c>\2a</c>, <c>\28</c>, <c>\29</c>, <c>\5c</c> and <c>\00</c>, so a login like <c>*)(uid=*</c> can't change the filter.
    /// Non-ASCII characters are escaped as their UTF-8 bytes too, which every server accepts.
    /// </summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\\': sb.Append(@"\5c"); break;
                case 0: sb.Append(@"\00"); break;
                case < 0x20 or > 0x7e:
                    var n = rune.EncodeToUtf8(utf8);
                    foreach (var b in utf8[..n]) sb.Append('\\').Append(b.ToString("x2"));
                    break;
                default: sb.Append((char)rune.Value); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The template with every <c>{0}</c> replaced by the escaped value (no other format syntax is interpreted).</summary>
    public static string Format(string template, string value) => template.Replace("{0}", Escape(value), StringComparison.Ordinal);
}
