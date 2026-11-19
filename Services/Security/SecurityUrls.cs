using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Security;

/// <summary>Night Owl integration settings (section <c>NightOwl</c>).</summary>
public sealed class NightOwlOptions
{
    /// <summary>Optional URL of a Night Owl dashboard (<c>NightOwl.Web</c>), linked from the Security pages.</summary>
    public string? DashboardUrl { get; set; }

    /// <summary>The dashboard URL when it is an absolute http(s) URL, else null.</summary>
    public string? SafeDashboardUrl =>
        Uri.TryCreate(DashboardUrl?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;
}

public static class SecurityUrls
{
    public static string Repo(Repository r, string? branch = null, string? finding = null)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(branch) && branch != r.DefaultBranch) q.Add("branch=" + Uri.EscapeDataString(branch));
        if (!string.IsNullOrEmpty(finding)) q.Add("finding=" + Uri.EscapeDataString(finding));
        return RepoUrls.Base(r) + "/security" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    public static string Finding(Repository r, string fingerprint) => Repo(r, null, fingerprint);
    public static string Settings(Repository r) => RepoUrls.Base(r) + "/settings/security";
    public static string TriageExport(Repository r) => RepoUrls.Base(r) + "/security/triage.json";
}

/// <summary>Display names for the security enums.</summary>
public static class SecurityLabels
{
    public static string Category(SecurityCategory c) => c switch
    {
        SecurityCategory.Dependency => "SCA vulnerability",
        SecurityCategory.License => "License",
        SecurityCategory.Code => "SAST",
        SecurityCategory.Secret => "Secret",
        SecurityCategory.Infrastructure => "IaC",
        SecurityCategory.Container => "Container",
        SecurityCategory.Dynamic => "DAST",
        SecurityCategory.Quality => "Quality",
        _ => c.ToString(),
    };

    /// <summary>Stable lowercase key (API, webhooks, query strings).</summary>
    public static string CategoryKey(SecurityCategory c) => c switch
    {
        SecurityCategory.Dependency => "sca",
        SecurityCategory.Code => "sast",
        SecurityCategory.Infrastructure => "iac",
        SecurityCategory.Dynamic => "dast",
        _ => c.ToString().ToLowerInvariant(),
    };

    public static SecurityCategory? ParseCategory(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        foreach (var c in Enum.GetValues<SecurityCategory>())
            if (CategoryKey(c).Equals(key.Trim(), StringComparison.OrdinalIgnoreCase) || c.ToString().Equals(key.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public static string Triage(SecurityTriageState s) => s switch
    {
        SecurityTriageState.FalsePositive => "False positive",
        SecurityTriageState.AcceptedRisk => "Accepted risk",
        SecurityTriageState.Confirmed => "Confirmed",
        SecurityTriageState.Fixed => "Fixed",
        _ => "Open",
    };

    public static string SeverityCss(SecuritySeverity s) => "sev-" + s.ToString().ToLowerInvariant();
}
