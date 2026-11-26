namespace ByteBench.Components.Account;

public static class AccountUrls
{
    /// <summary>
    /// Returns <paramref name="returnUrl"/> if it is a same-site path (see <see cref="IsLocal"/>), otherwise "/".
    /// Prevents open redirects after sign-in. Every post-login redirect (password, 2FA, SSO, SAML, OAuth) uses this.
    /// </summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        IsLocal(returnUrl) ? returnUrl! : "/";

    /// <summary>
    /// True only for a plain same-site path: starts with a single "/", has no control characters (browsers drop
    /// tab, CR and LF from URLs, so "/", TAB, "/evil.example" becomes "//evil.example"), no backslash anywhere
    /// (browsers treat it as "/"), and no percent-encoded slash, backslash or control character right after the
    /// leading "/" (a second decoding step downstream would turn it into "//host").
    /// </summary>
    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/') return false;
        foreach (var c in url)
            if (char.IsControl(c) || c == '\\' || (int)c is 0x2028 or 0x2029) return false;
        if (url.Length > 1 && url[1] == '/') return false;
        if (url.Length >= 4 && url[1] == '%')
        {
            var enc = url.Substring(2, 2).ToUpperInvariant();
            if (enc is "2F" or "5C" or "09" or "0A" or "0D" or "00") return false;
        }
        return true;
    }
}
