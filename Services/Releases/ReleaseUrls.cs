using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Releases;

/// <summary>Routes for tags, releases, release assets and source archives.</summary>
public static class ReleaseUrls
{
    public static string Tags(Repository r) => RepoUrls.Base(r) + "/tags";
    public static string List(Repository r) => RepoUrls.Base(r) + "/releases";
    public static string Detail(Repository r, int id) => $"{RepoUrls.Base(r)}/releases/{id}";
    public static string Edit(Repository r, int id) => $"{RepoUrls.Base(r)}/releases/{id}/edit";

    public static string New(Repository r, string? tag = null) =>
        RepoUrls.Base(r) + "/releases/new" + (string.IsNullOrEmpty(tag) ? "" : "?tag=" + EscapeRef(tag));

    /// <summary><c>/p/{KEY}/repos/{slug}/archive/{ref}.zip</c> or <c>.tar.gz</c>.</summary>
    public static string Archive(Repository r, string @ref, ArchiveFormat format) =>
        $"{RepoUrls.Base(r)}/archive/{EscapeRef(@ref)}{ArchiveFormats.Extension(format)}";

    public static string Asset(ReleaseAsset a) => $"/release-assets/{a.Id}/{Uri.EscapeDataString(a.FileName)}";

    /// <summary>Escapes each path segment of a ref but keeps the slashes (refs like <c>release/1.x</c>).</summary>
    public static string EscapeRef(string @ref) => string.Join('/', @ref.Split('/').Select(Uri.EscapeDataString));
}

public enum ArchiveFormat { Zip, TarGz }

public static class ArchiveFormats
{
    public static string Extension(ArchiveFormat f) => f == ArchiveFormat.Zip ? ".zip" : ".tar.gz";

    /// <summary>Splits <c>v1.0.zip</c> into (<c>v1.0</c>, Zip); null if the extension isn't supported.</summary>
    public static (string Ref, ArchiveFormat Format)? Parse(string path)
    {
        if (path.EndsWith(".tar.gz", StringComparison.Ordinal)) return (path[..^7], ArchiveFormat.TarGz);
        if (path.EndsWith(".tgz", StringComparison.Ordinal)) return (path[..^4], ArchiveFormat.TarGz);
        if (path.EndsWith(".zip", StringComparison.Ordinal)) return (path[..^4], ArchiveFormat.Zip);
        return null;
    }
}
