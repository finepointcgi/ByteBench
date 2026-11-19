using ByteBench.Data;
using ByteBench.Services.Ci;

namespace ByteBench.Services.Packages;

/// <summary>
/// URLs of a project's registry and the commands shown on the Packages page. <c>baseUrl</c> is the site's absolute URL
/// without a trailing slash (e.g. <c>https://bytebench.example.com</c>).
/// </summary>
public static class PackageUrls
{
    public static string Page(string projectKey) => $"/p/{projectKey}/packages";
    public static string Detail(string projectKey, int packageId) => $"/p/{projectKey}/packages/{packageId}";

    public static string NuGetIndex(string baseUrl, string projectKey) => $"{baseUrl}/packages/{projectKey}/nuget/index.json";
    public static string NpmRegistry(string baseUrl, string projectKey) => $"{baseUrl}/packages/{projectKey}/npm/";

    /// <summary>The registry host for <c>docker login</c> (host and port, no scheme).</summary>
    public static string RegistryHost(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Authority : baseUrl;

    /// <summary>The image prefix of a project: <c>{host}/{key in lowercase}</c>.</summary>
    public static string ImagePrefix(string baseUrl, string projectKey) => $"{RegistryHost(baseUrl)}/{projectKey.ToLowerInvariant()}";

    public static string Image(string baseUrl, string projectKey, string name) => $"{ImagePrefix(baseUrl, projectKey)}/{name}";

    /// <summary>The download URL of a version's file (the Packages page links to it; the browser session is accepted).</summary>
    public static string FileDownload(string baseUrl, string projectKey, Package p, PackageVersion v, PackageFile f) => p.Type switch
    {
        PackageType.NuGet => $"{baseUrl}/packages/{projectKey}/nuget/v3/flatcontainer/{p.NormalizedName}/{v.NormalizedVersion}/{f.FileName.ToLowerInvariant()}",
        _ => $"{baseUrl}/packages/{projectKey}/npm/{p.Name}/-/{f.FileName}",
    };

    /// <summary>The command that installs or pulls a version.</summary>
    public static string InstallCommand(string baseUrl, string projectKey, Package p, string? version) => p.Type switch
    {
        PackageType.NuGet => $"dotnet add package {p.Name}" + (version != null ? $" --version {version}" : ""),
        PackageType.Npm => $"npm install {p.Name}" + (version != null ? $"@{version}" : ""),
        _ => $"docker pull {Image(baseUrl, projectKey, p.Name)}:{version ?? "latest"}",
    };

    /// <summary>One-time setup for the package's client (source, registry or login).</summary>
    public static string SetupCommand(string baseUrl, string projectKey, PackageType type) => type switch
    {
        PackageType.NuGet => $"dotnet nuget add source {NuGetIndex(baseUrl, projectKey)} --name bytebench-{projectKey.ToLowerInvariant()} --username <user> --password <personal access token> --store-password-in-clear-text",
        PackageType.Npm => $"npm config set registry {NpmRegistry(baseUrl, projectKey)} && npm config set -- '//{RegistryHost(baseUrl)}/packages/{projectKey}/npm/:_authToken' <personal access token>",
        _ => $"docker login {RegistryHost(baseUrl)} -u <user> -p <personal access token>",
    };

    /// <summary>The command that publishes a package of this type.</summary>
    public static string PublishCommand(string baseUrl, string projectKey, PackageType type) => type switch
    {
        PackageType.NuGet => $"dotnet nuget push MyPackage.1.0.0.nupkg --source {NuGetIndex(baseUrl, projectKey)} --api-key <personal access token>",
        PackageType.Npm => $"npm publish --registry {NpmRegistry(baseUrl, projectKey)}",
        _ => $"docker push {ImagePrefix(baseUrl, projectKey)}/<image>:<tag>",
    };

    /// <summary>
    /// The non-secret registry variables a CI job gets (the job token itself goes in the secret env file as
    /// <c>BYTEBENCH_JOB_TOKEN</c> and <c>BYTEBENCH_REGISTRY_PASSWORD</c>).
    /// </summary>
    public static Dictionary<string, string> CiVariables(string baseUrl, string projectKey) => new()
    {
        ["BYTEBENCH_REGISTRY"] = RegistryHost(baseUrl),
        ["BYTEBENCH_REGISTRY_IMAGE"] = ImagePrefix(baseUrl, projectKey),
        ["BYTEBENCH_REGISTRY_USER"] = CiJobTokens.UserName,
        ["BYTEBENCH_NUGET_SOURCE_URL"] = NuGetIndex(baseUrl, projectKey),
        ["BYTEBENCH_NPM_REGISTRY_URL"] = NpmRegistry(baseUrl, projectKey),
    };
}
