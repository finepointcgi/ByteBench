using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Git;
using ByteBench.Services.Importing;
using ByteBench.Services.Mirroring;
using ByteBench.Services.Releases;
using ByteBench.Services.Webhooks;

namespace ByteBench.Api;

// ----- Management wire types: repositories, imports, mirrors, branch protection, releases, webhooks -----

/// <summary>Creates an empty repository (with an initial README commit on its default branch). <c>slug</c> defaults to the name, slugified.</summary>
public record CreateRepoRequest(string? Name = null, string? Slug = null, string? Description = null);

/// <summary>Only the properties you send change. <c>slug</c> renames the repository (the old address redirects); <c>defaultBranch</c> must be an existing branch.</summary>
public record UpdateRepoRequest(string? Name = null, string? Description = null, string? DefaultBranch = null, string? Slug = null);

/// <summary>Imports a repository from another Git server in the background. Credentials are used for the clone only (and kept, encrypted, when keepAsMirror is set).</summary>
public record ImportRepoRequest(string? Url = null, string? Username = null, string? Secret = null, string? Name = null, string? Slug = null,
    string? Description = null, string? DefaultBranch = null, bool? KeepAsMirror = null);

public record RepoImportDto(int Id, string Status, string SourceUrl, string Name, string Slug, string? DefaultBranch, bool KeepAsMirror, string? Error,
    int BranchCount, int TagCount, string? Repository, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>A pull or push mirror. The secret is write-only; <c>hasSecret</c> says whether one is stored.</summary>
public record MirrorRequest(string? Url = null, string? Username = null, string? Secret = null, bool? ClearSecret = null, int? IntervalMinutes = null,
    bool? OnlyProtectedBranches = null, bool? Enabled = null);

public record MirrorDto(int Id, string Direction, string Url, string? Username, bool HasSecret, bool Enabled, int IntervalMinutes, bool OnlyProtectedBranches,
    string LastStatus, DateTimeOffset? LastSyncAt, string? LastMessage);

public record BranchProtectionRuleDto(int Id, string Pattern, int RequiredApprovals, bool BlockDirectPushes, bool BlockForcePushes, bool BlockDeletion,
    bool RequireChecksToPass, bool DismissStaleApprovals, bool AllowAdminBypass, bool RequireThreadResolution, bool RequireCodeOwnerApproval,
    bool RequireSignedCommits, IReadOnlyList<int> DefaultReviewerIds, bool MergeQueueEnabled, int MergeQueueMaxBatch, bool MergeQueueRequireCi,
    MergeStrategy? MergeQueueStrategy, int MergeQueueTimeoutMinutes, int MergeQueueApprovalTimeoutMinutes);

/// <summary>On create, every property you leave out takes the UI's default (force pushes and deletion blocked, nothing else required). On PATCH, only the ones you send change.</summary>
public record BranchProtectionRequest(string? Pattern = null, int? RequiredApprovals = null, bool? BlockDirectPushes = null, bool? BlockForcePushes = null,
    bool? BlockDeletion = null, bool? RequireChecksToPass = null, bool? DismissStaleApprovals = null, bool? AllowAdminBypass = null,
    bool? RequireThreadResolution = null, bool? RequireCodeOwnerApproval = null, bool? RequireSignedCommits = null, List<int>? DefaultReviewerIds = null,
    bool? MergeQueueEnabled = null, int? MergeQueueMaxBatch = null, bool? MergeQueueRequireCi = null, MergeStrategy? MergeQueueStrategy = null,
    int? MergeQueueTimeoutMinutes = null, int? MergeQueueApprovalTimeoutMinutes = null);

public record UpdateReleaseRequest(string? Title = null, string? Notes = null, bool? Prerelease = null, bool? Draft = null);

/// <summary>A webhook. The secret is shown once (in the create and rotate responses) and is masked afterwards.</summary>
public record WebhookDto(int Id, string Url, IReadOnlyList<string> Events, bool Active, string Secret, int ConsecutiveFailures, DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt, DateTimeOffset? LastDeliveryAt);

public record SaveWebhookRequest(string? Url = null, List<string>? Events = null, bool? Active = null, string? Secret = null);

public record WebhookDeliveryDto(int Id, string DeliveryId, string Event, bool? Success, int? StatusCode, string? Error, int Attempts, int DurationMs,
    bool IsRedelivery, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public record WebhookSecretDto(string Secret);

/// <summary>
/// Repository management: create, edit, rename, archive and delete repositories, import and mirror them, branch protection rules, release
/// editing and project webhooks. All of it calls the services the repository pages call, so Maintainer+ permissions (<c>ManageRepositories</c>),
/// validation and audit entries match the UI. Request bodies reject unknown properties (400).
/// </summary>
public static class RepoManagementEndpoints
{
    public static void MapRepoManagementEndpoints(this RouteGroupBuilder api)
    {
        var p = api.MapGroup("/projects/{key}").WithTags("Repository management");
        p.MapPost("/repos", CreateRepo).WithName("CreateRepository").WithSummary("Create a repository (Maintainer+)")
            .WithDescription("Makes a bare repository with an initial README commit. 409 when the slug is taken in the project.")
            .WithJsonBody<CreateRepoRequest>().Produces<RepositoryDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        var r = p.MapGroup("/repos/{slug}");
        r.MapPatch("", UpdateRepo).WithName("UpdateRepository").WithSummary("Change a repository's name, description, default branch or slug (Maintainer+)")
            .WithJsonBody<UpdateRepoRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        r.MapPost("/archive", (HttpContext h, RepositoryService repos, RepositorySettingsService settings, ProjectAccess access, PublicBaseUrl u, string key, string slug) =>
                SetArchived(h, repos, settings, access, u, key, slug, true))
            .WithName("ArchiveRepository").WithSummary("Archive a repository: it becomes read-only (Maintainer+)");
        r.MapPost("/unarchive", (HttpContext h, RepositoryService repos, RepositorySettingsService settings, ProjectAccess access, PublicBaseUrl u, string key, string slug) =>
                SetArchived(h, repos, settings, access, u, key, slug, false))
            .WithName("UnarchiveRepository").WithSummary("Make an archived repository writable again (Maintainer+)");
        r.MapDelete("", DeleteRepo).WithName("DeleteRepository").WithSummary("Delete a repository (Maintainer+)")
            .WithDescription("Irreversible. Like the UI's type-to-confirm dialog, pass ?confirm=<the repository's name> (see GET). Archive instead to keep the history.")
            .Produces(StatusCodes.Status204NoContent);

        // import
        p.MapPost("/repo-imports", StartImport).WithName("ImportRepository").WithSummary("Import a repository from another Git server (Maintainer+)")
            .WithDescription("Returns 202 with the import job; poll GET /repo-imports/{id}. keepAsMirror turns the result into a pull mirror.")
            .WithJsonBody<ImportRepoRequest>().Produces<RepoImportDto>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status409Conflict);
        p.MapGet("/repo-imports", ListImports).WithName("ListRepositoryImports").WithSummary("Recent imports of the project (Maintainer+)");
        p.MapGet("/repo-imports/{id:int}", GetImport).WithName("GetRepositoryImport").WithSummary("Status of an import (Maintainer+)");
        p.MapPost("/repo-imports/{id:int}/cancel", CancelImport).WithName("CancelRepositoryImport").WithSummary("Stop a queued or running import (Maintainer+)");

        // mirrors
        r.MapGet("/mirrors", ListMirrors).WithName("ListMirrors").WithSummary("List a repository's pull and push mirrors (Maintainer+)");
        r.MapPut("/mirrors/pull", SavePullMirror).WithName("SavePullMirror").WithSummary("Create or replace the pull mirror (Maintainer+)")
            .WithJsonBody<MirrorRequest>().Produces<MirrorDto>();
        r.MapPost("/mirrors/push", CreatePushMirror).WithName("CreatePushMirror").WithSummary("Add a push mirror (Maintainer+)")
            .WithJsonBody<MirrorRequest>().Produces<MirrorDto>(StatusCodes.Status201Created);
        r.MapPut("/mirrors/push/{id:int}", UpdatePushMirror).WithName("UpdatePushMirror").WithSummary("Change a push mirror (Maintainer+)")
            .WithJsonBody<MirrorRequest>();
        r.MapDelete("/mirrors/{id:int}", DeleteMirror).WithName("DeleteMirror").WithSummary("Remove a mirror (Maintainer+)").Produces(StatusCodes.Status204NoContent);

        // branch protection
        r.MapGet("/branch-protection", ListRules).WithName("ListBranchProtectionRules").WithSummary("List branch protection rules");
        r.MapPost("/branch-protection", CreateRule).WithName("CreateBranchProtectionRule").WithSummary("Create a branch protection rule (Maintainer+)")
            .WithDescription("pattern is a branch name or a glob (* and ?). 409 when a rule for the pattern exists.")
            .WithJsonBody<BranchProtectionRequest>().Produces<BranchProtectionRuleDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        r.MapPatch("/branch-protection/{id:int}", UpdateRule).WithName("UpdateBranchProtectionRule").WithSummary("Change a branch protection rule (Maintainer+)")
            .WithJsonBody<BranchProtectionRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        r.MapDelete("/branch-protection/{id:int}", DeleteRule).WithName("DeleteBranchProtectionRule").WithSummary("Delete a branch protection rule (Maintainer+)")
            .Produces(StatusCodes.Status204NoContent);

        // releases
        r.MapPatch("/releases/{id:int}", UpdateRelease).WithName("UpdateRelease").WithSummary("Edit a release's title, notes or flags (Developer+)")
            .WithDescription("The tag can't change. Setting draft to false publishes a draft.").WithJsonBody<UpdateReleaseRequest>();
        r.MapPost("/releases/{id:int}/publish", PublishRelease).WithName("PublishRelease").WithSummary("Publish a draft release (Developer+)");
        r.MapDelete("/releases/{id:int}", DeleteRelease).WithName("DeleteRelease").WithSummary("Delete a release and its assets (Maintainer+)")
            .WithDescription("?deleteTag=true also deletes the tag (unless protected).").Produces(StatusCodes.Status204NoContent);

        // webhooks
        var w = p.MapGroup("/webhooks").WithTags("Webhooks");
        w.MapGet("", ListWebhooks).WithName("ListWebhooks").WithSummary("List the project's webhooks (Maintainer+)");
        w.MapPost("", CreateWebhook).WithName("CreateWebhook").WithSummary("Create a webhook (Maintainer+)")
            .WithDescription("events: names such as push, issue.created, pull_request.merged. A blank secret is generated; it is returned here once.")
            .WithJsonBody<SaveWebhookRequest>().Produces<WebhookDto>(StatusCodes.Status201Created);
        w.MapPatch("/{id:int}", UpdateWebhook).WithName("UpdateWebhook").WithSummary("Change a webhook's URL, events, active flag or secret (Maintainer+)")
            .WithJsonBody<SaveWebhookRequest>();
        w.MapDelete("/{id:int}", DeleteWebhook).WithName("DeleteWebhook").WithSummary("Delete a webhook (Maintainer+)").Produces(StatusCodes.Status204NoContent);
        w.MapPost("/{id:int}/rotate-secret", RotateSecret).WithName("RotateWebhookSecret").WithSummary("Replace the signing secret; returned once (Maintainer+)")
            .Produces<WebhookSecretDto>();
        w.MapPost("/{id:int}/ping", PingWebhook).WithName("PingWebhook").WithSummary("Queue a test delivery (Maintainer+)").Produces(StatusCodes.Status202Accepted);
        w.MapGet("/{id:int}/deliveries", ListDeliveries).WithName("ListWebhookDeliveries").WithSummary("Recent deliveries of a webhook (Maintainer+)");
    }

    private static async Task<Repository> RequireRepoAsync(RepositoryService repos, string key, string slug) =>
        await repos.GetRepositoryAsync(key, slug) ?? throw ApiException.NotFound($"Repository '{key.ToUpperInvariant()}/{slug}' not found.");

    // ----------------------------------------------------------------------------------------------- repositories

    private static async Task<IResult> CreateRepo(HttpContext http, IssueService issues, RepositoryService repos, ProjectAccess access, ApiCaller caller, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageRepositories);
        var body = await Management.ReadAsync<CreateRepoRequest>(http.Request);
        var name = Management.Required(body.Name, "name");
        var repo = await Management.RunAsync(() => repos.CreateAsync(project.Id, name, body.Slug, body.Description, caller.User));
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{project.Key}/repos/{repo.Slug}", Dto.Repository(repo, baseUrl));
    }

    private static async Task<RepositoryDto> UpdateRepo(HttpContext http, RepositoryService repos, RepositorySettingsService settings, ProjectAccess access, PublicBaseUrl url,
        string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        var body = await Management.ReadAsync<UpdateRepoRequest>(http.Request);
        var name = body.Name == null ? repo.Name : Management.Required(body.Name, "name");
        var description = body.Description ?? repo.Description;
        var branch = string.IsNullOrWhiteSpace(body.DefaultBranch) ? repo.DefaultBranch : body.DefaultBranch.Trim();
        if (branch != repo.DefaultBranch && !await repos.Git(repo, path => GitOps.Exists(path) && GitOps.BranchExists(path, branch)))
            throw ApiException.Unprocessable($"The branch '{branch}' doesn't exist; the default branch must be an existing branch.");
        await Management.RunAsync(() => settings.UpdateGeneralAsync(repo.Id, name, description, branch));
        var current = slug;
        if (!string.IsNullOrWhiteSpace(body.Slug) && !body.Slug.Trim().Equals(repo.Slug, StringComparison.OrdinalIgnoreCase))
            current = await Management.RunAsync(() => settings.RenameAsync(repo.Id, body.Slug));
        return Dto.Repository(await RequireRepoAsync(repos, key, current), url.For(http.Request));
    }

    private static async Task<RepositoryDto> SetArchived(HttpContext http, RepositoryService repos, RepositorySettingsService settings, ProjectAccess access, PublicBaseUrl url,
        string key, string slug, bool archived)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        await Management.RunAsync(() => settings.SetArchivedAsync(repo.Id, archived));
        return Dto.Repository(await RequireRepoAsync(repos, key, slug), url.For(http.Request));
    }

    private static async Task<IResult> DeleteRepo(RepositoryService repos, RepositorySettingsService settings, ProjectAccess access, string key, string slug, string? confirm)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        if (string.IsNullOrEmpty(confirm)) throw ApiException.Unprocessable($"Pass ?confirm={repo.Name} (the repository's name) to confirm the deletion.");
        await Management.RunAsync(() => settings.DeleteAsync(repo.Id, confirm));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- import

    private static RepoImportDto ImportDtoOf(RepositoryImport i) => new(i.Id, i.Status.ToString().ToLowerInvariant(), i.SourceUrl, i.Name, i.Slug, i.DefaultBranch,
        i.KeepAsMirror, i.Error, i.BranchCount, i.TagCount, i.Repository?.Slug, Dto.Utc(i.CreatedAt), Dto.Utc(i.FinishedAt));

    private static async Task<IResult> StartImport(HttpContext http, IssueService issues, ImportService imports, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageRepositories);
        var body = await Management.ReadAsync<ImportRepoRequest>(http.Request);
        var input = new ImportInput
        {
            Url = Management.Required(body.Url, "url"), Username = body.Username, Secret = body.Secret, Name = Management.Required(body.Name, "name"),
            Slug = body.Slug, Description = body.Description, DefaultBranch = body.DefaultBranch, KeepAsMirror = body.KeepAsMirror ?? false,
        };
        var job = await Management.RunAsync(() => imports.StartAsync(project.Id, input));
        return TypedResults.Accepted($"{url.For(http.Request)}/api/v1/projects/{project.Key}/repo-imports/{job.Id}", ImportDtoOf(job));
    }

    private static async Task<List<RepoImportDto>> ListImports(HttpContext http, IssueService issues, ImportService imports, ProjectAccess access, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageRepositories);
        return paging.Apply(http, await imports.ListAsync(project.Id, 100), ImportDtoOf);
    }

    private static async Task<RepoImportDto> GetImport(IssueService issues, ImportService imports, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageRepositories);
        var job = await imports.GetAsync(id);
        return job != null && job.ProjectId == project.Id ? ImportDtoOf(job) : throw ApiException.NotFound($"Import {id} not found in {project.Key}.");
    }

    private static async Task<RepoImportDto> CancelImport(IssueService issues, ImportService imports, ProjectAccess access, string key, int id)
    {
        await GetImport(issues, imports, access, key, id);
        await Management.RunAsync(() => imports.CancelAsync(id));
        return await GetImport(issues, imports, access, key, id);
    }

    // ----------------------------------------------------------------------------------------------- mirrors

    private static MirrorDto MirrorDtoOf(RepositoryMirror m) => new(m.Id, m.Direction.ToString().ToLowerInvariant(), m.Url, m.Username, !string.IsNullOrEmpty(m.ProtectedSecret),
        m.Enabled, m.IntervalMinutes, m.OnlyProtectedBranches, m.LastStatus.ToString().ToLowerInvariant(), Dto.Utc(m.LastSyncAt), m.LastMessage);

    private static async Task<Repository> ManagedRepoAsync(RepositoryService repos, ProjectAccess access, string key, string slug)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.ManageRepositories);
        return repo;
    }

    private static MirrorInput MirrorInputOf(MirrorRequest b) => new()
    {
        Url = Management.Required(b.Url, "url"), Username = b.Username, Secret = b.Secret, ClearSecret = b.ClearSecret ?? false,
        IntervalMinutes = b.IntervalMinutes ?? 60, OnlyProtectedBranches = b.OnlyProtectedBranches ?? false, Enabled = b.Enabled ?? true,
    };

    private static async Task<List<MirrorDto>> ListMirrors(HttpContext http, RepositoryService repos, MirrorService mirrors, ProjectAccess access, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        return paging.Apply(http, await mirrors.ListAsync(repo.Id), MirrorDtoOf);
    }

    private static async Task<MirrorDto> SavePullMirror(HttpContext http, RepositoryService repos, MirrorService mirrors, ProjectAccess access, string key, string slug)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        var body = await Management.ReadAsync<MirrorRequest>(http.Request);
        return MirrorDtoOf(await Management.RunAsync(() => mirrors.SavePullAsync(repo.Id, MirrorInputOf(body))));
    }

    private static async Task<IResult> CreatePushMirror(HttpContext http, RepositoryService repos, MirrorService mirrors, ProjectAccess access, PublicBaseUrl url, string key, string slug)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        var body = await Management.ReadAsync<MirrorRequest>(http.Request);
        var m = await Management.RunAsync(() => mirrors.SavePushAsync(repo.Id, null, MirrorInputOf(body)));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{key.ToUpperInvariant()}/repos/{repo.Slug}/mirrors", MirrorDtoOf(m));
    }

    private static async Task<MirrorDto> UpdatePushMirror(HttpContext http, RepositoryService repos, MirrorService mirrors, ProjectAccess access, string key, string slug, int id)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        if ((await mirrors.ListAsync(repo.Id)).All(m => m.Id != id || m.Direction != MirrorDirection.Push)) throw ApiException.NotFound($"Push mirror {id} not found.");
        var body = await Management.ReadAsync<MirrorRequest>(http.Request);
        return MirrorDtoOf(await Management.RunAsync(() => mirrors.SavePushAsync(repo.Id, id, MirrorInputOf(body))));
    }

    private static async Task<IResult> DeleteMirror(RepositoryService repos, MirrorService mirrors, ProjectAccess access, string key, string slug, int id)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        if ((await mirrors.ListAsync(repo.Id)).All(m => m.Id != id)) throw ApiException.NotFound($"Mirror {id} not found.");
        await Management.RunAsync(() => mirrors.DeleteAsync(repo.Id, id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- branch protection

    private static BranchProtectionRuleDto RuleDtoOf(BranchProtectionRule r) => new(r.Id, r.Pattern, r.RequiredApprovals, r.BlockDirectPushes, r.BlockForcePushes, r.BlockDeletion,
        r.RequireChecksToPass, r.DismissStaleApprovals, r.AllowAdminBypass, r.RequireThreadResolution, r.RequireCodeOwnerApproval, r.RequireSignedCommits,
        r.DefaultReviewerIds, r.MergeQueueEnabled, r.MergeQueueMaxBatch, r.MergeQueueRequireCi, r.MergeQueueStrategy, r.MergeQueueTimeoutMinutes,
        r.MergeQueueApprovalTimeoutMinutes);

    private static BranchProtectionRule Apply(BranchProtectionRule rule, BranchProtectionRequest b)
    {
        rule.Pattern = b.Pattern ?? rule.Pattern;
        rule.RequiredApprovals = b.RequiredApprovals ?? rule.RequiredApprovals;
        rule.BlockDirectPushes = b.BlockDirectPushes ?? rule.BlockDirectPushes;
        rule.BlockForcePushes = b.BlockForcePushes ?? rule.BlockForcePushes;
        rule.BlockDeletion = b.BlockDeletion ?? rule.BlockDeletion;
        rule.RequireChecksToPass = b.RequireChecksToPass ?? rule.RequireChecksToPass;
        rule.DismissStaleApprovals = b.DismissStaleApprovals ?? rule.DismissStaleApprovals;
        rule.AllowAdminBypass = b.AllowAdminBypass ?? rule.AllowAdminBypass;
        rule.RequireThreadResolution = b.RequireThreadResolution ?? rule.RequireThreadResolution;
        rule.RequireCodeOwnerApproval = b.RequireCodeOwnerApproval ?? rule.RequireCodeOwnerApproval;
        rule.RequireSignedCommits = b.RequireSignedCommits ?? rule.RequireSignedCommits;
        rule.DefaultReviewerIds = b.DefaultReviewerIds ?? rule.DefaultReviewerIds;
        rule.MergeQueueEnabled = b.MergeQueueEnabled ?? rule.MergeQueueEnabled;
        rule.MergeQueueMaxBatch = b.MergeQueueMaxBatch ?? rule.MergeQueueMaxBatch;
        rule.MergeQueueRequireCi = b.MergeQueueRequireCi ?? rule.MergeQueueRequireCi;
        rule.MergeQueueStrategy = b.MergeQueueStrategy ?? rule.MergeQueueStrategy;
        rule.MergeQueueTimeoutMinutes = b.MergeQueueTimeoutMinutes ?? rule.MergeQueueTimeoutMinutes;
        rule.MergeQueueApprovalTimeoutMinutes = b.MergeQueueApprovalTimeoutMinutes ?? rule.MergeQueueApprovalTimeoutMinutes;
        return rule;
    }

    private static async Task<List<BranchProtectionRuleDto>> ListRules(HttpContext http, RepositoryService repos, BranchProtectionService protection, string key, string slug)
    {
        var paging = Paging.From(http.Request);
        var repo = await RequireRepoAsync(repos, key, slug);
        return paging.Apply(http, await protection.ListAsync(repo.Id), RuleDtoOf);
    }

    private static async Task<IResult> CreateRule(HttpContext http, RepositoryService repos, BranchProtectionService protection, ProjectAccess access, PublicBaseUrl url,
        string key, string slug)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        var body = await Management.ReadAsync<BranchProtectionRequest>(http.Request);
        Management.Required(body.Pattern, "pattern");
        var saved = await Management.RunAsync(() => protection.SaveAsync(repo.Id, Apply(new BranchProtectionRule { RepositoryId = repo.Id }, body)));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{repo.Project.Key}/repos/{repo.Slug}/branch-protection", RuleDtoOf(saved));
    }

    private static async Task<BranchProtectionRuleDto> UpdateRule(HttpContext http, RepositoryService repos, BranchProtectionService protection, ProjectAccess access,
        string key, string slug, int id)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        var existing = (await protection.ListAsync(repo.Id)).FirstOrDefault(r => r.Id == id) ?? throw ApiException.NotFound($"Rule {id} not found.");
        var body = await Management.ReadAsync<BranchProtectionRequest>(http.Request);
        if (body.Pattern != null) Management.Required(body.Pattern, "pattern");
        return RuleDtoOf(await Management.RunAsync(() => protection.SaveAsync(repo.Id, Apply(existing, body))));
    }

    private static async Task<IResult> DeleteRule(RepositoryService repos, BranchProtectionService protection, ProjectAccess access, string key, string slug, int id)
    {
        var repo = await ManagedRepoAsync(repos, access, key, slug);
        if ((await protection.ListAsync(repo.Id)).All(r => r.Id != id)) throw ApiException.NotFound($"Rule {id} not found.");
        await Management.RunAsync(() => protection.DeleteAsync(repo.Id, id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- releases

    private static async Task<ReleaseDto> UpdateRelease(HttpContext http, RepositoryService repos, ReleaseService releases, ProjectAccess access, PublicBaseUrl url,
        string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        var existing = await releases.GetAsync(repo, id) ?? throw ApiException.NotFound($"Release {id} not found.");
        var body = await Management.ReadAsync<UpdateReleaseRequest>(http.Request);
        await Management.RunAsync(() => releases.UpdateAsync(repo, id, new ReleaseInput
        {
            TagName = existing.TagName, Title = body.Title ?? existing.Title, Notes = body.Notes ?? existing.Notes,
            IsPrerelease = body.Prerelease ?? existing.IsPrerelease, IsDraft = body.Draft ?? existing.IsDraft,
        }));
        return ReleaseDtos.Release(await releases.GetAsync(repo, id) ?? throw ApiException.NotFound("Release not found."), url.For(http.Request));
    }

    private static async Task<ReleaseDto> PublishRelease(HttpContext http, RepositoryService repos, ReleaseService releases, ProjectAccess access, PublicBaseUrl url,
        string key, string slug, int id)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        await Management.RunAsync(() => releases.PublishAsync(repo, id));
        return ReleaseDtos.Release(await releases.GetAsync(repo, id) ?? throw ApiException.NotFound("Release not found."), url.For(http.Request));
    }

    private static async Task<IResult> DeleteRelease(RepositoryService repos, ReleaseService releases, ProjectAccess access, string key, string slug, int id, bool? deleteTag)
    {
        var repo = await RequireRepoAsync(repos, key, slug);
        await access.RequireAsync(repo.ProjectId, Permission.DeleteBranch);
        if (await releases.GetAsync(repo, id) == null) throw ApiException.NotFound($"Release {id} not found.");
        await Management.RunAsync(() => releases.DeleteAsync(repo, id, deleteTag == true));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- webhooks

    private static WebhookDto HookDtoOf(Webhook h, string? secret = null) => new(h.Id, h.Url, h.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        h.IsActive, secret ?? h.Secret, h.ConsecutiveFailures, Dto.Utc(h.DisabledAt), Dto.Utc(h.CreatedAt), Dto.Utc(h.LastDeliveryAt));

    private static async Task<Project> HookProjectAsync(IssueService issues, ProjectAccess access, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, WebhookService.RequiredPermission);
        return project;
    }

    private static async Task<Webhook> HookAsync(WebhookService hooks, Project project, int id) =>
        (await hooks.ListAsync(project.Id)).FirstOrDefault(h => h.Id == id) ?? throw ApiException.NotFound($"Webhook {id} not found in {project.Key}.");

    private static async Task<List<WebhookDto>> ListWebhooks(HttpContext http, IssueService issues, WebhookService hooks, ProjectAccess access, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await HookProjectAsync(issues, access, key);
        return paging.Apply(http, await hooks.ListAsync(project.Id), h => HookDtoOf(h));
    }

    private static async Task<IResult> CreateWebhook(HttpContext http, IssueService issues, WebhookService hooks, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await HookProjectAsync(issues, access, key);
        var body = await Management.ReadAsync<SaveWebhookRequest>(http.Request);
        var hook = await Management.RunAsync(() => hooks.CreateAsync(project.Id, Management.Required(body.Url, "url"), body.Secret,
            body.Events ?? throw ApiException.Unprocessable("events is required (at least one)."), body.Active ?? true));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/webhooks/{hook.Id}", HookDtoOf(hook));
    }

    private static async Task<WebhookDto> UpdateWebhook(HttpContext http, IssueService issues, WebhookService hooks, ProjectAccess access, string key, int id)
    {
        var project = await HookProjectAsync(issues, access, key);
        var existing = await HookAsync(hooks, project, id);
        var body = await Management.ReadAsync<SaveWebhookRequest>(http.Request);
        var updated = await Management.RunAsync(() => hooks.UpdateAsync(project.Id, id, body.Url == null ? existing.Url : Management.Required(body.Url, "url"), body.Secret,
            body.Events ?? existing.Events.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(), body.Active ?? existing.IsActive));
        return HookDtoOf(updated);
    }

    private static async Task<IResult> DeleteWebhook(IssueService issues, WebhookService hooks, ProjectAccess access, string key, int id)
    {
        var project = await HookProjectAsync(issues, access, key);
        await HookAsync(hooks, project, id);
        await Management.RunAsync(() => hooks.DeleteAsync(project.Id, id));
        return TypedResults.NoContent();
    }

    private static async Task<WebhookSecretDto> RotateSecret(IssueService issues, WebhookService hooks, ProjectAccess access, string key, int id)
    {
        var project = await HookProjectAsync(issues, access, key);
        await HookAsync(hooks, project, id);
        return new WebhookSecretDto(await Management.RunAsync(() => hooks.RegenerateSecretAsync(project.Id, id)));
    }

    private static async Task<IResult> PingWebhook(IssueService issues, WebhookService hooks, ProjectAccess access, string key, int id)
    {
        var project = await HookProjectAsync(issues, access, key);
        await HookAsync(hooks, project, id);
        await Management.RunAsync(() => hooks.PingAsync(project.Id, id));
        return TypedResults.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<List<WebhookDeliveryDto>> ListDeliveries(HttpContext http, IssueService issues, WebhookService hooks, ProjectAccess access, string key, int id)
    {
        var paging = Paging.From(http.Request);
        var project = await HookProjectAsync(issues, access, key);
        await HookAsync(hooks, project, id);
        var all = await Management.RunAsync(() => hooks.RecentDeliveriesAsync(project.Id, id, 100));
        return paging.Apply(http, all, d => new WebhookDeliveryDto(d.Id, d.DeliveryId, d.Event, d.Success, d.StatusCode, d.Error, d.Attempts, d.DurationMs,
            d.IsRedelivery, Dto.Utc(d.CreatedAt), Dto.Utc(d.CompletedAt)));
    }
}
