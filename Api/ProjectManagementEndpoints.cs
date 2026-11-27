using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Fields;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Api;

// ----- Management wire types: projects, members, labels, components, versions, custom fields, workflow -----

/// <summary><c>key</c> 2-10 uppercase letters/digits starting with a letter; <c>groupId</c> creates the project inside a group you can create in.</summary>
public record CreateProjectRequest(string? Key = null, string? Name = null, string? Description = null, ProjectVisibility? Visibility = null, int? GroupId = null);

/// <summary>Only the properties you send change. An empty <c>description</c> clears it.</summary>
public record UpdateProjectRequest(string? Name = null, string? Description = null, ProjectVisibility? Visibility = null);

public record MemberDto(UserDto User, ProjectRole Role);

public record AddMemberRequest(string? User = null, ProjectRole? Role = null);

public record ChangeRoleRequest(ProjectRole? Role = null);

public record SaveLabelRequest(string? Name = null, string? Color = null);

public record SaveComponentRequest(string? Name = null, string? Description = null, string? Lead = null, string? DefaultAssignee = null);

public record SaveVersionRequest(string? Name = null, string? Description = null, DateOnly? StartDate = null, DateOnly? ReleaseDate = null, bool? Released = null);

public record SaveFieldRequest(string? Key = null, string? Name = null, string? Description = null, CustomFieldType? Type = null, bool? Required = null,
    string? DefaultValue = null, List<string>? Options = null, List<IssueType>? IssueTypes = null);

public record SaveStatusRequest(string? Name = null, string? Color = null, IssueStatus? Category = null);

public record MoveStatusRequest(int? Delta = null);

public record TransitionRequest(int From, int To);

public record SetTransitionsRequest(List<TransitionRequest>? Transitions = null);

public record CreateGroupRequest(string? Name = null, string? Path = null, string? Description = null, ProjectVisibility? Visibility = null, string? Color = null, int? ParentId = null);

public record UpdateGroupRequest(string? Name = null, string? Description = null, ProjectVisibility? Visibility = null, string? Color = null);

/// <summary>
/// Project structure management under <c>/api/v1/projects</c> and <c>/groups</c>. Every route calls the same service methods the Blazor pages call
/// (<see cref="IssueService"/>, <see cref="MemberService"/>, <see cref="ComponentVersionService"/>, <see cref="CustomFieldService"/>,
/// <see cref="WorkflowService"/>, <see cref="GroupService"/>), so permissions, validation and audit entries are identical. Bodies reject unknown
/// properties (400); a missing thing is 404, a duplicate 409, a rule the service refuses 422.
/// </summary>
public static partial class ProjectManagementEndpoints
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRx();

    public static void MapProjectManagementEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/projects", CreateProject).WithName("CreateProject").WithTags("Projects").WithSummary("Create a project")
            .WithDescription("You become its Owner; it starts with the default labels and workflow. 409 when the key is taken. With groupId, you need to be allowed to create projects in that group.")
            .WithJsonBody<CreateProjectRequest>().Produces<ProjectDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        var p = api.MapGroup("/projects/{key}").WithTags("Projects");
        p.MapPatch("", UpdateProject).WithName("UpdateProject").WithSummary("Change a project's name, description or visibility (Owner)")
            .WithJsonBody<UpdateProjectRequest>().ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        p.MapDelete("", DeleteProject).WithName("DeleteProject").WithSummary("Delete a project (Owner)")
            .WithDescription("Irreversible: issues, repositories and everything else go. Like the UI's type-to-confirm dialog, you must pass ?confirm=<the project key>. (Projects can't be archived, only deleted.)")
            .Produces(StatusCodes.Status204NoContent);

        // members
        p.MapGet("/members", ListMembers).WithName("ListProjectMembers").WithSummary("List project members and their roles");
        p.MapPost("/members", AddMember).WithName("AddProjectMember").WithSummary("Add a member by username or e-mail (Owner)")
            .WithJsonBody<AddMemberRequest>().Produces<MemberDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        p.MapPatch("/members/{username}", ChangeMemberRole).WithName("ChangeProjectMemberRole").WithSummary("Change a member's role (Owner)")
            .WithDescription("The last Owner can't be demoted (422).").WithJsonBody<ChangeRoleRequest>();
        p.MapDelete("/members/{username}", RemoveMember).WithName("RemoveProjectMember").WithSummary("Remove a member (Owner)")
            .WithDescription("The last Owner can't be removed (422).").Produces(StatusCodes.Status204NoContent);

        // labels
        p.MapPost("/labels", CreateLabel).WithName("CreateLabel").WithSummary("Create a label (Maintainer+)")
            .WithJsonBody<SaveLabelRequest>().Produces<LabelDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        p.MapPatch("/labels/{id:int}", UpdateLabel).WithName("UpdateLabel").WithSummary("Rename or recolour a label (Maintainer+)")
            .WithJsonBody<SaveLabelRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        p.MapDelete("/labels/{id:int}", DeleteLabel).WithName("DeleteLabel").WithSummary("Delete a label; it is removed from issues (Maintainer+)")
            .Produces(StatusCodes.Status204NoContent);

        // components
        p.MapPost("/components", CreateComponent).WithName("CreateComponent").WithSummary("Create a component (Maintainer+)")
            .WithDescription("lead and defaultAssignee are usernames of people who can see the project.")
            .WithJsonBody<SaveComponentRequest>().Produces<ComponentDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        p.MapPatch("/components/{id:int}", UpdateComponent).WithName("UpdateComponent").WithSummary("Change a component (Maintainer+)")
            .WithDescription("Only the properties you send change; an empty string clears description, lead or defaultAssignee.")
            .WithJsonBody<SaveComponentRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        p.MapDelete("/components/{id:int}", DeleteComponent).WithName("DeleteComponent").WithSummary("Delete a component (Maintainer+)").Produces(StatusCodes.Status204NoContent);

        // versions
        p.MapPost("/versions", CreateVersion).WithName("CreateVersion").WithSummary("Create a version (Maintainer+)")
            .WithDescription("released: true marks it released today unless releaseDate is given.")
            .WithJsonBody<SaveVersionRequest>().Produces<VersionDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        p.MapPatch("/versions/{id:int}", UpdateVersion).WithName("UpdateVersion").WithSummary("Change a version, or mark it released/unreleased (Maintainer+)")
            .WithJsonBody<SaveVersionRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        p.MapDelete("/versions/{id:int}", DeleteVersion).WithName("DeleteVersion").WithSummary("Delete a version (Maintainer+)").Produces(StatusCodes.Status204NoContent);

        // custom fields
        p.MapPost("/fields", CreateField).WithName("CreateCustomField").WithSummary("Create a custom field (Maintainer+)")
            .WithDescription("type: text, longText, number, date, singleSelect, multiSelect, user, checkbox or url. key: lowercase letters, digits and _, starting with a letter.")
            .WithJsonBody<SaveFieldRequest>().Produces<CustomFieldDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        p.MapPatch("/fields/{id:int}", UpdateField).WithName("UpdateCustomField").WithSummary("Change a custom field (Maintainer+)")
            .WithDescription("The type can't change once issues have values.").WithJsonBody<SaveFieldRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        p.MapDelete("/fields/{id:int}", DeleteField).WithName("DeleteCustomField").WithSummary("Delete a custom field and every value of it (Maintainer+)").Produces(StatusCodes.Status204NoContent);

        // workflow
        p.MapPost("/workflow/statuses", AddStatus).WithName("AddWorkflowStatus").WithSummary("Add a workflow status (Owner)")
            .WithDescription("category is the built-in status it counts as: backlog, todo, inProgress, inReview or done.")
            .WithJsonBody<SaveStatusRequest>().Produces<WorkflowStatusDto>(StatusCodes.Status201Created);
        p.MapPatch("/workflow/statuses/{id:int}", UpdateStatus).WithName("UpdateWorkflowStatus").WithSummary("Rename, recolour or recategorise a status (Owner)")
            .WithJsonBody<SaveStatusRequest>();
        p.MapPost("/workflow/statuses/{id:int}/move", MoveStatus).WithName("MoveWorkflowStatus").WithSummary("Move a status one place left (delta -1) or right (+1) (Owner)")
            .WithJsonBody<MoveStatusRequest>();
        p.MapDelete("/workflow/statuses/{id:int}", DeleteStatus).WithName("DeleteWorkflowStatus").WithSummary("Delete a status (Owner)")
            .WithDescription("If issues have it, pass ?moveIssuesTo=<status id> (422 otherwise).").Produces(StatusCodes.Status204NoContent);
        p.MapPut("/workflow/transitions", SetTransitions).WithName("SetWorkflowTransitions").WithSummary("Replace the allowed status transitions (Owner)")
            .WithDescription("A status with no transitions listed may move to any status. Send an empty list to allow everything.")
            .WithJsonBody<SetTransitionsRequest>();

        // groups
        var g = api.MapGroup("/groups").WithTags("Groups");
        g.MapPost("", CreateGroup).WithName("CreateGroup").WithSummary("Create a group or subgroup")
            .WithDescription("You become its Owner. parentId creates a subgroup (you need to be allowed to create in the parent). 409 when the path is taken.")
            .WithJsonBody<CreateGroupRequest>().Produces<GroupDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        g.MapPatch("/{group}", UpdateGroup).WithName("UpdateGroup").WithSummary("Change a group's name, description, visibility or colour (Owner)")
            .WithJsonBody<UpdateGroupRequest>();
        g.MapGet("/{group}/members", ListGroupMembers).WithName("ListGroupMembers").WithSummary("List a group's members (direct and inherited)");
        g.MapPost("/{group}/members", AddGroupMember).WithName("AddGroupMember").WithSummary("Add a direct group member by username or e-mail (Owner)")
            .WithJsonBody<AddMemberRequest>().Produces<MemberDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict);
        g.MapPatch("/{group}/members/{username}", ChangeGroupMemberRole).WithName("ChangeGroupMemberRole").WithSummary("Change a direct group member's role (Owner)")
            .WithJsonBody<ChangeRoleRequest>();
        g.MapDelete("/{group}/members/{username}", RemoveGroupMember).WithName("RemoveGroupMember").WithSummary("Remove a direct group member (Owner)").Produces(StatusCodes.Status204NoContent);
    }

    // ----------------------------------------------------------------------------------------------- projects

    private static async Task<IResult> CreateProject(HttpContext http, IssueService issues, ProjectAccess access, PublicBaseUrl url)
    {
        var body = await Management.ReadAsync<CreateProjectRequest>(http.Request);
        var project = new Project
        {
            Key = Management.Required(body.Key, "key").ToUpperInvariant(),
            Name = Management.Required(body.Name, "name"),
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            Visibility = body.Visibility ?? ProjectVisibility.Private,
            GroupId = body.GroupId,
        };
        Validate(project); // the same data annotations the New project form enforces
        var created = await Management.RunAsync(() => issues.CreateProjectAsync(project));
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{created.Key}", Dto.Project(created, await access.GetRoleAsync(created.Id), baseUrl));
    }

    private static void Validate(object model)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true))
            throw ApiException.Unprocessable(string.Join(" ", results.Select(r => r.ErrorMessage)));
    }

    private static async Task<ProjectDto> UpdateProject(HttpContext http, IssueService issues, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageProjectSettings);
        var body = await Management.ReadAsync<UpdateProjectRequest>(http.Request);
        var name = body.Name == null ? project.Name : Management.Required(body.Name, "name");
        var description = body.Description == null ? project.Description : string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        var visibility = body.Visibility ?? project.Visibility;
        Validate(new Project { Key = project.Key, Name = name, Description = description });
        await Management.RunAsync(() => issues.UpdateProjectAsync(project.Id, name, description, visibility));
        var updated = await IssueEndpoints.RequireProjectAsync(issues, key);
        return Dto.Project(updated, await access.GetRoleAsync(updated.Id), url.For(http.Request));
    }

    private static async Task<IResult> DeleteProject(IssueService issues, ProjectAccess access, string key, string? confirm)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.DeleteProject);
        if (!string.Equals(confirm, project.Key, StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unprocessable($"Pass ?confirm={project.Key} to confirm the deletion.");
        await Management.RunAsync(() => issues.DeleteProjectAsync(project.Id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- members

    private static async Task<List<MemberDto>> ListMembers(HttpContext http, IssueService issues, MemberService members, string key)
    {
        var paging = Paging.From(http.Request);
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        return paging.Apply(http, await members.GetMembersAsync(project.Id), m => new MemberDto(Dto.User(m.User), m.Role));
    }

    private static async Task<IResult> AddMember(HttpContext http, IssueService issues, MemberService members, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageMembers);
        var body = await Management.ReadAsync<AddMemberRequest>(http.Request);
        var user = Management.Required(body.User, "user");
        var role = body.Role ?? throw ApiException.Unprocessable("role is required (viewer, developer, maintainer or owner).");
        var member = await Management.RunAsync(() => members.AddMemberAsync(project.Id, user, role));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/members", new MemberDto(Dto.User(member.User), member.Role));
    }

    private static async Task<User> FindUserAsync(UserManager<User> users, string username) =>
        await users.FindByNameAsync(username) ?? throw ApiException.NotFound($"No user '{username}'.");

    private static async Task<MemberDto> ChangeMemberRole(HttpContext http, IssueService issues, MemberService members, ProjectAccess access, UserManager<User> users,
        string key, string username)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageMembers);
        var body = await Management.ReadAsync<ChangeRoleRequest>(http.Request);
        var role = body.Role ?? throw ApiException.Unprocessable("role is required (viewer, developer, maintainer or owner).");
        var user = await FindUserAsync(users, username);
        await Management.RunAsync(() => members.ChangeRoleAsync(project.Id, user.Id, role));
        return new MemberDto(Dto.User(user), role);
    }

    private static async Task<IResult> RemoveMember(IssueService issues, MemberService members, ProjectAccess access, UserManager<User> users, string key, string username)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageMembers);
        var user = await FindUserAsync(users, username);
        await Management.RunAsync(() => members.RemoveMemberAsync(project.Id, user.Id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- labels

    private static string LabelColor(string? color, string fallback)
    {
        if (color == null) return fallback;
        return ColorRx().IsMatch(color) ? color.ToLowerInvariant() : throw ApiException.Unprocessable("color must look like #1a2b3c.");
    }

    private static async Task<IResult> CreateLabel(HttpContext http, IssueService issues, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var body = await Management.ReadAsync<SaveLabelRequest>(http.Request);
        var label = new Label { ProjectId = project.Id, Name = Management.Required(body.Name, "name"), Color = LabelColor(body.Color, "#6b7280") };
        var saved = await Management.RunAsync(() => issues.SaveLabelAsync(label));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/labels", Dto.Label(saved));
    }

    private static async Task<LabelDto> UpdateLabel(HttpContext http, IssueService issues, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var existing = project.Labels.FirstOrDefault(l => l.Id == id && l.ProjectId == project.Id) ?? throw ApiException.NotFound($"Label {id} not found in {project.Key}.");
        var body = await Management.ReadAsync<SaveLabelRequest>(http.Request);
        var label = new Label
        {
            Id = id, ProjectId = project.Id,
            Name = body.Name == null ? existing.Name : Management.Required(body.Name, "name"),
            Color = LabelColor(body.Color, existing.Color),
        };
        return Dto.Label(await Management.RunAsync(() => issues.SaveLabelAsync(label)));
    }

    private static async Task<IResult> DeleteLabel(IssueService issues, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        if (!project.Labels.Any(l => l.Id == id && l.ProjectId == project.Id)) throw ApiException.NotFound($"Label {id} not found in {project.Key}.");
        await Management.RunAsync(() => issues.DeleteLabelAsync(id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- components

    private static async Task<int?> UserIdAsync(UserManager<User> users, string? username, string field)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        return (await users.FindByNameAsync(username.Trim()))?.Id ?? throw ApiException.Unprocessable($"{field}: no user '{username.Trim()}'.");
    }

    private static ComponentDto ComponentDtoOf(ComponentSummary c) => new(c.Component.Id, c.Component.Name, c.Component.Description,
        Dto.UserOrNull(c.Component.Lead), Dto.UserOrNull(c.Component.DefaultAssignee), c.OpenIssues, c.TotalIssues);

    private static async Task<IResult> CreateComponent(HttpContext http, IssueService issues, ComponentVersionService cv, ProjectAccess access, UserManager<User> users,
        PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var body = await Management.ReadAsync<SaveComponentRequest>(http.Request);
        var c = new ProjectComponent
        {
            ProjectId = project.Id, Name = Management.Required(body.Name, "name"), Description = body.Description,
            LeadId = await UserIdAsync(users, body.Lead, "lead"), DefaultAssigneeId = await UserIdAsync(users, body.DefaultAssignee, "defaultAssignee"),
        };
        var saved = await Management.RunAsync(() => cv.SaveComponentAsync(c));
        var dto = ComponentDtoOf((await cv.GetComponentsAsync(project.Id)).First(x => x.Component.Id == saved.Id));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/components", dto);
    }

    private static async Task<ComponentDto> UpdateComponent(HttpContext http, IssueService issues, ComponentVersionService cv, ProjectAccess access, UserManager<User> users,
        string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var existing = (await cv.GetComponentsAsync(project.Id)).FirstOrDefault(x => x.Component.Id == id)?.Component ?? throw ApiException.NotFound($"Component {id} not found in {project.Key}.");
        var body = await Management.ReadAsync<SaveComponentRequest>(http.Request);
        var c = new ProjectComponent
        {
            Id = id, ProjectId = project.Id,
            Name = body.Name == null ? existing.Name : Management.Required(body.Name, "name"),
            Description = body.Description == null ? existing.Description : body.Description,
            LeadId = body.Lead == null ? existing.LeadId : await UserIdAsync(users, body.Lead, "lead"),
            DefaultAssigneeId = body.DefaultAssignee == null ? existing.DefaultAssigneeId : await UserIdAsync(users, body.DefaultAssignee, "defaultAssignee"),
        };
        await Management.RunAsync(() => cv.SaveComponentAsync(c));
        return ComponentDtoOf((await cv.GetComponentsAsync(project.Id)).First(x => x.Component.Id == id));
    }

    private static async Task<IResult> DeleteComponent(IssueService issues, ComponentVersionService cv, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        if ((await cv.GetComponentsAsync(project.Id)).All(x => x.Component.Id != id)) throw ApiException.NotFound($"Component {id} not found in {project.Key}.");
        await Management.RunAsync(() => cv.DeleteComponentAsync(id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- versions

    private static VersionDto VersionDtoOf(VersionSummary v, string baseUrl) => new(v.Version.Id, v.Version.Name, v.Version.Description,
        Dto.Date(v.Version.StartDate), Dto.Date(v.Version.ReleaseDate), v.Version.IsReleased, v.Open, v.Done, v.Percent,
        v.Release == null ? null : baseUrl + v.Release.Url);

    private static async Task<IResult> CreateVersion(HttpContext http, IssueService issues, ComponentVersionService cv, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var body = await Management.ReadAsync<SaveVersionRequest>(http.Request);
        var v = new ProjectVersion
        {
            ProjectId = project.Id, Name = Management.Required(body.Name, "name"), Description = body.Description,
            StartDate = Management.Date(body.StartDate), ReleaseDate = Management.Date(body.ReleaseDate), IsReleased = body.Released ?? false,
        };
        var saved = await Management.RunAsync(() => cv.SaveVersionAsync(v));
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/projects/{project.Key}/versions", VersionDtoOf((await cv.GetVersionsAsync(project.Id)).First(x => x.Version.Id == saved.Id), baseUrl));
    }

    private static async Task<VersionDto> UpdateVersion(HttpContext http, IssueService issues, ComponentVersionService cv, ProjectAccess access, PublicBaseUrl url,
        string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var existing = (await cv.GetVersionsAsync(project.Id)).FirstOrDefault(x => x.Version.Id == id)?.Version ?? throw ApiException.NotFound($"Version {id} not found in {project.Key}.");
        var body = await Management.ReadAsync<SaveVersionRequest>(http.Request);
        var v = new ProjectVersion
        {
            Id = id, ProjectId = project.Id,
            Name = body.Name == null ? existing.Name : Management.Required(body.Name, "name"),
            Description = body.Description ?? existing.Description,
            StartDate = body.StartDate != null ? Management.Date(body.StartDate) : existing.StartDate,
            ReleaseDate = body.ReleaseDate != null ? Management.Date(body.ReleaseDate) : existing.ReleaseDate,
            IsReleased = body.Released ?? existing.IsReleased,
        };
        await Management.RunAsync(() => cv.SaveVersionAsync(v));
        return VersionDtoOf((await cv.GetVersionsAsync(project.Id)).First(x => x.Version.Id == id), url.For(http.Request));
    }

    private static async Task<IResult> DeleteVersion(IssueService issues, ComponentVersionService cv, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        if ((await cv.GetVersionsAsync(project.Id)).All(x => x.Version.Id != id)) throw ApiException.NotFound($"Version {id} not found in {project.Key}.");
        await Management.RunAsync(() => cv.DeleteVersionAsync(id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- custom fields

    private static CustomFieldDto FieldDtoOf(CustomField f) => new(f.Id, f.Key, f.Name, f.Description,
        JsonNamingPolicy.CamelCase.ConvertName(f.Type.ToString()), f.Required, f.DefaultValue, f.Options, f.IssueTypes);

    private static async Task<IResult> CreateField(HttpContext http, IssueService issues, CustomFieldService fields, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var body = await Management.ReadAsync<SaveFieldRequest>(http.Request);
        var f = new CustomField
        {
            ProjectId = project.Id, Key = Management.Required(body.Key, "key"), Name = Management.Required(body.Name, "name"),
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            Type = body.Type ?? throw ApiException.Unprocessable("type is required."), Required = body.Required ?? false,
            DefaultValue = string.IsNullOrEmpty(body.DefaultValue) ? null : body.DefaultValue, Options = body.Options ?? [], IssueTypes = body.IssueTypes ?? [],
        };
        var saved = await Management.RunAsync(() => fields.SaveFieldAsync(f));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/fields", FieldDtoOf(saved));
    }

    private static async Task<CustomFieldDto> UpdateField(HttpContext http, IssueService issues, CustomFieldService fields, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        var existing = (await fields.GetFieldsAsync(project.Id)).FirstOrDefault(x => x.Id == id) ?? throw ApiException.NotFound($"Field {id} not found in {project.Key}.");
        var body = await Management.ReadAsync<SaveFieldRequest>(http.Request);
        var f = new CustomField
        {
            Id = id, ProjectId = project.Id, Order = existing.Order, CreatedAt = existing.CreatedAt,
            Key = body.Key == null ? existing.Key : Management.Required(body.Key, "key"),
            Name = body.Name == null ? existing.Name : Management.Required(body.Name, "name"),
            Description = body.Description == null ? existing.Description : string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            Type = body.Type ?? existing.Type, Required = body.Required ?? existing.Required,
            DefaultValue = body.DefaultValue == null ? existing.DefaultValue : string.IsNullOrEmpty(body.DefaultValue) ? null : body.DefaultValue,
            Options = body.Options ?? existing.Options, IssueTypes = body.IssueTypes ?? existing.IssueTypes,
        };
        return FieldDtoOf(await Management.RunAsync(() => fields.SaveFieldAsync(f)));
    }

    private static async Task<IResult> DeleteField(IssueService issues, CustomFieldService fields, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageLabelsAndMilestones);
        if ((await fields.GetFieldsAsync(project.Id)).All(x => x.Id != id)) throw ApiException.NotFound($"Field {id} not found in {project.Key}.");
        await Management.RunAsync(() => fields.DeleteFieldAsync(id));
        return TypedResults.NoContent();
    }

    // ----------------------------------------------------------------------------------------------- workflow

    private static async Task<WorkflowStatus> StatusAsync(WorkflowService workflows, int projectId, string key, int id) =>
        (await workflows.GetAsync(projectId)).Find(id) ?? throw ApiException.NotFound($"Status {id} not found in {key}.");

    private static WorkflowStatusDto StatusDtoOf(WorkflowStatus s) => new(s.Id, s.Name, s.Color, s.Order, s.Category);

    private static async Task<IResult> AddStatus(HttpContext http, IssueService issues, WorkflowService workflows, ProjectAccess access, PublicBaseUrl url, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageProjectSettings);
        var body = await Management.ReadAsync<SaveStatusRequest>(http.Request);
        var status = await Management.RunAsync(() => workflows.AddStatusAsync(project.Id, Management.Required(body.Name, "name"), body.Color ?? "#64748b",
            body.Category ?? throw ApiException.Unprocessable("category is required (backlog, todo, inProgress, inReview or done).")));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/projects/{project.Key}/workflow", StatusDtoOf(status));
    }

    private static async Task<WorkflowStatusDto> UpdateStatus(HttpContext http, IssueService issues, WorkflowService workflows, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageProjectSettings);
        var existing = await StatusAsync(workflows, project.Id, project.Key, id);
        var body = await Management.ReadAsync<SaveStatusRequest>(http.Request);
        await Management.RunAsync(() => workflows.UpdateStatusAsync(project.Id, id, body.Name == null ? existing.Name : Management.Required(body.Name, "name"),
            body.Color ?? existing.Color, body.Category ?? existing.Category));
        return StatusDtoOf(await StatusAsync(workflows, project.Id, project.Key, id));
    }

    private static async Task<WorkflowDto> MoveStatus(HttpContext http, IssueService issues, WorkflowService workflows, ProjectAccess access, string key, int id)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageProjectSettings);
        await StatusAsync(workflows, project.Id, project.Key, id);
        var body = await Management.ReadAsync<MoveStatusRequest>(http.Request);
        if (body.Delta is not (-1 or 1)) throw ApiException.Unprocessable("delta must be -1 (left) or 1 (right).");
        await Management.RunAsync(() => workflows.MoveStatusAsync(project.Id, id, body.Delta.Value));
        return WorkflowDtoOf(await workflows.GetAsync(project.Id));
    }

    private static async Task<IResult> DeleteStatus(IssueService issues, WorkflowService workflows, ProjectAccess access, string key, int id, int? moveIssuesTo)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageProjectSettings);
        await StatusAsync(workflows, project.Id, project.Key, id);
        await Management.RunAsync(() => workflows.DeleteStatusAsync(project.Id, id, moveIssuesTo));
        return TypedResults.NoContent();
    }

    private static WorkflowDto WorkflowDtoOf(ProjectWorkflow wf) => new(wf.Statuses.Select(StatusDtoOf).ToList(),
        wf.Transitions.OrderBy(t => t.FromStatusId).ThenBy(t => t.ToStatusId).Select(t => new WorkflowTransitionDto(t.FromStatusId, t.ToStatusId)).ToList());

    private static async Task<WorkflowDto> SetTransitions(HttpContext http, IssueService issues, WorkflowService workflows, ProjectAccess access, string key)
    {
        var project = await IssueEndpoints.RequireProjectAsync(issues, key);
        await access.RequireAsync(project.Id, Permission.ManageProjectSettings);
        var body = await Management.ReadAsync<SetTransitionsRequest>(http.Request);
        var allowed = new Dictionary<int, HashSet<int>>();
        foreach (var t in body.Transitions ?? throw ApiException.Unprocessable("transitions is required (a list of {from, to}; empty allows every move)."))
        {
            if (!allowed.TryGetValue(t.From, out var set)) allowed[t.From] = set = [];
            set.Add(t.To);
        }
        await Management.RunAsync(() => workflows.SetTransitionsAsync(project.Id, allowed));
        return WorkflowDtoOf(await workflows.GetAsync(project.Id));
    }

    // ----------------------------------------------------------------------------------------------- groups

    private static async Task<IResult> CreateGroup(HttpContext http, GroupService groups, ProjectAccess access, PublicBaseUrl url)
    {
        var body = await Management.ReadAsync<CreateGroupRequest>(http.Request);
        var name = Management.Required(body.Name, "name");
        var path = Management.Required(body.Path, "path");
        var created = await Management.RunAsync(() => groups.CreateAsync(name, path, body.Description, body.Visibility ?? ProjectVisibility.Private, body.Color, body.ParentId));
        var baseUrl = url.For(http.Request);
        return TypedResults.Created($"{baseUrl}/api/v1/groups/{Uri.EscapeDataString(created.FullPath)}", GroupEndpoints.ToDto(created, await access.GetGroupRoleAsync(created.Id), baseUrl));
    }

    private static async Task<GroupDto> UpdateGroup(HttpContext http, GroupService groups, ProjectAccess access, PublicBaseUrl url, string group)
    {
        var g = await GroupEndpoints.RequireGroupAsync(groups, group);
        await access.RequireAsync(g.Id, GroupPermission.ManageGroupSettings);
        var body = await Management.ReadAsync<UpdateGroupRequest>(http.Request);
        await Management.RunAsync(() => groups.UpdateAsync(g.Id, body.Name == null ? g.Name : Management.Required(body.Name, "name"),
            body.Description == null ? g.Description : body.Description, body.Visibility ?? g.Visibility, body.Color));
        var updated = await groups.GetAsync(g.Id) ?? g;
        return GroupEndpoints.ToDto(updated, await access.GetGroupRoleAsync(updated.Id), url.For(http.Request));
    }

    private static async Task<List<GroupMemberDto>> ListGroupMembers(HttpContext http, GroupService groups, string group)
    {
        var paging = Paging.From(http.Request);
        var g = await GroupEndpoints.RequireGroupAsync(groups, group);
        return paging.Apply(http, await groups.GetMembersAsync(g.Id), m => new GroupMemberDto(Dto.User(m.User), m.Role, m.Inherited, m.GroupPath));
    }

    private static async Task<IResult> AddGroupMember(HttpContext http, GroupService groups, ProjectAccess access, PublicBaseUrl url, string group)
    {
        var g = await GroupEndpoints.RequireGroupAsync(groups, group);
        await access.RequireAsync(g.Id, GroupPermission.ManageGroupMembers);
        var body = await Management.ReadAsync<AddMemberRequest>(http.Request);
        var user = Management.Required(body.User, "user");
        var role = body.Role ?? throw ApiException.Unprocessable("role is required (viewer, developer, maintainer or owner).");
        var member = await Management.RunAsync(() => groups.AddMemberAsync(g.Id, user, role));
        return TypedResults.Created($"{url.For(http.Request)}/api/v1/groups/{Uri.EscapeDataString(g.FullPath)}/members", new MemberDto(Dto.User(member.User), member.Role));
    }

    private static async Task<MemberDto> ChangeGroupMemberRole(HttpContext http, GroupService groups, ProjectAccess access, UserManager<User> users, string group, string username)
    {
        var g = await GroupEndpoints.RequireGroupAsync(groups, group);
        await access.RequireAsync(g.Id, GroupPermission.ManageGroupMembers);
        var body = await Management.ReadAsync<ChangeRoleRequest>(http.Request);
        var role = body.Role ?? throw ApiException.Unprocessable("role is required (viewer, developer, maintainer or owner).");
        var user = await FindUserAsync(users, username);
        await Management.RunAsync(() => groups.ChangeMemberRoleAsync(g.Id, user.Id, role));
        return new MemberDto(Dto.User(user), role);
    }

    private static async Task<IResult> RemoveGroupMember(GroupService groups, ProjectAccess access, UserManager<User> users, string group, string username)
    {
        var g = await GroupEndpoints.RequireGroupAsync(groups, group);
        await access.RequireAsync(g.Id, GroupPermission.ManageGroupMembers);
        var user = await FindUserAsync(users, username);
        await Management.RunAsync(() => groups.RemoveMemberAsync(g.Id, user.Id));
        return TypedResults.NoContent();
    }
}

public record GroupMemberDto(UserDto User, ProjectRole Role, bool Inherited, string GroupPath);
