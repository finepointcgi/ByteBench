using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Packages;

namespace ByteBench.Api;

public record PackageSummaryDto(int Id, PackageType Type, string Name, int VersionCount, string? LatestVersion, long SizeBytes, DateTimeOffset UpdatedAt, string WebUrl);

public record PackageFileDto(int Id, string FileName, long SizeBytes, string Sha256, string? DownloadUrl);

public record PackageVersionDto(int Id, string Version, long SizeBytes, string? ManifestDigest, UserDto? PublishedBy, int? PipelineId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PackageFileDto> Files, string InstallCommand);

/// <summary>A package with its versions (newest first). <c>distTags</c> is npm's tag to version map (empty for other types).</summary>
public record PackageDetailDto(int Id, PackageType Type, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string WebUrl,
    IReadOnlyList<PackageVersionDto> Versions, IReadOnlyDictionary<string, string> DistTags);

/// <summary>The project's registry switch and cleanup policy, with the URLs clients use. <c>keepLatest</c> null: keep every version.</summary>
public record PackageSettingsDto(bool Enabled, int? KeepLatest, int MaxKeepLatest, string NuGetSource, string NpmRegistry, string ContainerRegistry, long UsageBytes);

/// <summary>PUT body: <c>enabled</c> is required; <c>keepLatest</c> (1-1000) turns the cleanup policy on, null or omitted turns it off.</summary>
public record PackageSettingsRequest(bool? Enabled = null, int? KeepLatest = null);

/// <summary>The project's package registry: packages and versions (read), deleting them and the registry settings (Maintainer+).</summary>
public static class PackageEndpoints
{
    public static void MapPackageEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects/{key}/packages").WithTags("Packages");
        g.MapGet("", ListPackages).WithName("ListPackages").WithSummary("List the project's packages")
            .WithDescription("?type= (nuget, npm or container) and ?q= (name contains) filter the list. Newest first. Publishing happens with the registry protocols (dotnet nuget push, npm publish, docker push), not here.");
        g.MapGet("/{id:int}", GetPackage).WithName("GetPackage").WithSummary("Get a package with its versions and files");
        g.MapDelete("/{id:int}", DeletePackage).WithName("DeletePackage").WithSummary("Delete a package and all its versions")
            .WithDescription("Needs Maintainer+. Audited and sent as the package.deleted webhook, like the UI.");
        g.MapDelete("/{id:int}/versions/{versionId:int}", DeleteVersion).WithName("DeletePackageVersion").WithSummary("Delete one version (or container tag)")
            .WithDescription("Needs Maintainer+. Audited and sent as the package.deleted webhook, like the UI.");
        g.MapGet("/settings", GetSettings).WithName("GetPackageSettings").WithSummary("The registry settings and cleanup policy")
            .WithDescription("Needs Maintainer+.");
        g.MapPut("/settings", PutSettings).WithName("SetPackageSettings").WithSummary("Turn the registry on or off and set the cleanup policy")
            .WithDescription("Needs Maintainer+. Lowering keepLatest deletes the older versions of every package straight away. Turning the registry off deletes nothing.")
            .WithJsonBody<PackageSettingsRequest>();
    }

    private static string Web(string baseUrl, string key, int id) => baseUrl + PackageUrls.Detail(key, id);

    private static async Task<List<PackageSummaryDto>> ListPackages(HttpContext http, IssueService issues, PackageService packages, PublicBaseUrl url, string key, string? type, string? q)
    {
        var paging = Paging.From(http.Request);
        PackageType? filter = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<PackageType>(type.Trim(), true, out var t) || !Enum.IsDefined(t))
                throw ApiException.BadRequest("type must be nuget, npm or container.");
            filter = t;
        }
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var rows = (await packages.ListAsync(project.Id)).Where(p => filter == null || p.Type == filter)
            .Where(p => string.IsNullOrWhiteSpace(q) || p.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var b = url.For(http.Request);
        return paging.Apply(http, rows, p => new PackageSummaryDto(p.Id, p.Type, p.Name, p.VersionCount, p.LatestVersion, p.Size, Dto.Utc(p.UpdatedAt), Web(b, project.Key, p.Id)));
    }

    private static async Task<PackageDetailDto> GetPackage(HttpContext http, IssueService issues, PackageService packages, PublicBaseUrl url, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var p = await packages.GetAsync(project.Id, id) ?? throw ApiException.NotFound($"Package {id} not found.");
        var b = url.For(http.Request);
        var tags = p.Type == PackageType.Npm ? await packages.DistTagsAsync(p.Id) : [];
        return new PackageDetailDto(p.Id, p.Type, p.Name, Dto.Utc(p.CreatedAt), Dto.Utc(p.UpdatedAt), Web(b, project.Key, p.Id),
            p.Versions.Select(v => new PackageVersionDto(v.Id, v.Version, v.Size, v.ManifestDigest, Dto.UserOrNull(v.PublishedBy), v.PipelineId,
                Dto.Utc(v.CreatedAt), Dto.Utc(v.UpdatedAt),
                v.Files.OrderBy(f => f.FileName).Select(f => new PackageFileDto(f.Id, f.FileName, f.Size, f.Sha256,
                    p.Type == PackageType.Container ? null : PackageUrls.FileDownload(b, project.Key, p, v, f))).ToList(),
                PackageUrls.InstallCommand(b, project.Key, p, v.Version))).ToList(),
            tags);
    }

    private static async Task<IResult> DeletePackage(IssueService issues, PackageService packages, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        if (await packages.GetAsync(project.Id, id) == null) throw ApiException.NotFound($"Package {id} not found.");
        await packages.DeletePackageAsync(project.Id, id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> DeleteVersion(IssueService issues, PackageService packages, string key, int id, int versionId)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var p = await packages.GetAsync(project.Id, id) ?? throw ApiException.NotFound($"Package {id} not found.");
        if (p.Versions.All(v => v.Id != versionId)) throw ApiException.NotFound($"Version {versionId} not found in package {id}.");
        await packages.DeleteVersionAsync(project.Id, versionId);
        return TypedResults.NoContent();
    }

    private static async Task<PackageSettingsDto> SettingsAsync(HttpContext http, PackageService packages, PublicBaseUrl url, Project project)
    {
        var s = await packages.GetSettingsAsync(project.Id);
        var b = url.For(http.Request);
        return new PackageSettingsDto(s.Enabled, s.KeepLatest, PackageService.MaxKeepLatest, PackageUrls.NuGetIndex(b, project.Key),
            PackageUrls.NpmRegistry(b, project.Key), PackageUrls.ImagePrefix(b, project.Key), await packages.ProjectUsageAsync(project.Id));
    }

    private static async Task<PackageSettingsDto> GetSettings(HttpContext http, IssueService issues, PackageService packages, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.DeletePackages);
        return await SettingsAsync(http, packages, url, project);
    }

    private static async Task<PackageSettingsDto> PutSettings(HttpContext http, IssueService issues, PackageService packages, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        var body = await ApiResults.ReadBodyAsync<PackageSettingsRequest>(http.Request);
        if (body.Enabled is not { } enabled) throw ApiException.Unprocessable("enabled is required.");
        await packages.SaveSettingsAsync(project.Id, enabled, body.KeepLatest);
        return await SettingsAsync(http, packages, url, project);
    }
}
