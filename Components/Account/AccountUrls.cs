namespace ByteBench.Components.Account;

internal static class AccountUrls
{
    /// <summary>
    /// Returns <paramref name="returnUrl"/> if it is a local path (starts with "/", but not "//" or "/\"),
    /// otherwise "/". Prevents open redirects after sign-in.
    /// </summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        IsLocal(returnUrl) ? returnUrl! : "/";

    public static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
