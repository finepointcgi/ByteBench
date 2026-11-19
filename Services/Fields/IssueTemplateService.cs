using ByteBench.Data;
using ByteBench.Services.Git;
using Microsoft.EntityFrameworkCore;

namespace ByteBench.Services.Fields;

/// <summary>A template offered on the new-issue form: one defined in the project, or a file in its first repository.</summary>
/// <param name="Id"><c>t{id}</c> for a project template, <c>repo:{file name}</c> for a repository one.</param>
public sealed record IssueTemplateChoice(string Id, string Name, IssueType Type, string? TitlePrefix, string? Body, IReadOnlyList<int> LabelIds, string? Source);

/// <summary>
/// Issue templates: defined per project (Maintainer+), plus Markdown files in <c>.bytebench/issue_templates/</c> on the
/// default branch of the project's first repository, with optional GitHub-style front matter
/// (<c>name</c>, <c>title</c>, <c>labels</c>, <c>type</c>).
/// </summary>
public class IssueTemplateService(IDbContextFactory<AppDbContext> dbFactory, ProjectAccess access, RepoStorage repoStorage)
{
    public const string RepoFolder = ".bytebench/issue_templates";
    /// <summary>The folder before the product was renamed from GitBench; still read, after <see cref="RepoFolder"/>.</summary>
    public const string LegacyRepoFolder = ".gitbench/issue_templates";
    private const int MaxRepoTemplates = 20;

    public async Task<List<IssueTemplate>> GetTemplatesAsync(int projectId)
    {
        if (!await access.CanAsync(projectId, Permission.ViewProject)) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.IssueTemplates.AsNoTracking().Where(t => t.ProjectId == projectId).OrderBy(t => t.Order).ThenBy(t => t.Name).ToListAsync();
    }

    /// <summary>Project templates first, then repository ones (labels matched by name against the project's labels).</summary>
    public async Task<List<IssueTemplateChoice>> GetChoicesAsync(Project project)
    {
        var list = (await GetTemplatesAsync(project.Id))
            .Select(t => new IssueTemplateChoice($"t{t.Id}", t.Name, t.Type, t.TitlePrefix, t.Body, t.LabelIds, null)).ToList();
        if (list.Count == 0 && !await access.CanAsync(project.Id, Permission.ViewProject)) return [];
        try { list.AddRange(await GetRepoChoicesAsync(project)); }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException) { /* an unreadable repo just has no templates */ }
        return list;
    }

    private async Task<List<IssueTemplateChoice>> GetRepoChoicesAsync(Project project)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var repo = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == project.Id).OrderBy(r => r.Id).FirstOrDefaultAsync();
        if (repo == null) return [];
        var path = repoStorage.PathFor(project.Key, repo.Slug);
        if (!Directory.Exists(path)) return [];
        var files = (GitOps.ListTree(path, repo.DefaultBranch, RepoFolder, withLastCommit: false) ?? [])
            .Concat(GitOps.ListTree(path, repo.DefaultBranch, LegacyRepoFolder, withLastCommit: false) ?? []).ToList();
        if (files.Count == 0) return [];
        var result = new List<IssueTemplateChoice>();
        foreach (var f in files.Where(f => !f.IsDirectory && f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Take(MaxRepoTemplates))
        {
            if (GitOps.GetBlob(path, repo.DefaultBranch, f.Path) is not { IsBinary: false, TooLarge: false, Text: { } text }) continue;
            var parsed = ParseRepoTemplate(f.Name, text);
            var labelIds = project.Labels.Where(l => parsed.Labels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).Select(l => l.Id).ToList();
            result.Add(new IssueTemplateChoice("repo:" + f.Name, parsed.Name, parsed.Type, parsed.Title, parsed.Body, labelIds, $"{repo.Slug}/{f.Path}"));
        }
        return result;
    }

    public sealed record RepoTemplate(string Name, IssueType Type, string? Title, string Body, IReadOnlyList<string> Labels);

    /// <summary>Parses a template file: optional front matter between <c>---</c> lines, then the Markdown body.</summary>
    public static RepoTemplate ParseRepoTemplate(string fileName, string text)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ');
        name = name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : fileName;
        string? title = null;
        var type = IssueType.Task;
        var labels = new List<string>();
        var body = text.Replace("\r\n", "\n");
        if (body.StartsWith("---\n"))
        {
            var end = body.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in body[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = Unquote(line[(colon + 1)..].Trim());
                    switch (key)
                    {
                        case "name" when value.Length > 0: name = value; break;
                        case "title": title = value.Length > 0 ? value : null; break;
                        case "type" when Enum.TryParse<IssueType>(value, true, out var t): type = t; break;
                        case "labels":
                            labels = value.Trim('[', ']').Split(',').Select(l => Unquote(l.Trim())).Where(l => l.Length > 0).ToList();
                            break;
                    }
                }
                var after = body.IndexOf('\n', end + 1);
                body = after < 0 ? "" : body[(after + 1)..];
            }
        }
        if (name.Length > 80) name = name[..80];
        if (title?.Length > 60) title = title[..60];
        return new RepoTemplate(name, type, title, body.Trim('\n'), labels);
    }

    private static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;

    public async Task<IssueTemplate> SaveAsync(IssueTemplate t)
    {
        await access.RequireAsync(t.ProjectId, Permission.ManageLabelsAndMilestones);
        t.Name = (t.Name ?? "").Trim();
        if (t.Name.Length is 0 or > 80) throw new InvalidOperationException("A template needs a name (at most 80 characters).");
        t.TitlePrefix = string.IsNullOrEmpty(t.TitlePrefix?.Trim()) ? null : t.TitlePrefix.TrimStart();
        if (t.TitlePrefix?.Length > 60) throw new InvalidOperationException("The title prefix can be at most 60 characters.");
        if (t.Body?.Length > 20_000) throw new InvalidOperationException("The body can be at most 20000 characters.");
        if (!Enum.IsDefined(t.Type)) throw new InvalidOperationException("Unknown issue type.");
        await using var db = await dbFactory.CreateDbContextAsync();
        IssueTemplate row;
        if (t.Id == 0)
        {
            row = new IssueTemplate
            {
                ProjectId = t.ProjectId,
                Order = (await db.IssueTemplates.Where(x => x.ProjectId == t.ProjectId).MaxAsync(x => (int?)x.Order) ?? 0) + 1,
            };
            db.IssueTemplates.Add(row);
        }
        else row = await db.IssueTemplates.FirstOrDefaultAsync(x => x.Id == t.Id && x.ProjectId == t.ProjectId)
                   ?? throw new ForbiddenException("Template not found in this project.");
        (row.Name, row.Type, row.TitlePrefix, row.Body, row.LabelIds) = (t.Name, t.Type, t.TitlePrefix, string.IsNullOrWhiteSpace(t.Body) ? null : t.Body, t.LabelIds.Distinct().ToList());
        await db.SaveChangesAsync();
        t.Id = row.Id;
        return row;
    }

    public async Task DeleteAsync(int templateId)
    {
        var projectId = await access.ProjectOf(d => d.IssueTemplates.Where(t => t.Id == templateId).Select(t => (int?)t.ProjectId));
        await access.RequireAsync(projectId, Permission.ManageLabelsAndMilestones);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.IssueTemplates.Where(t => t.Id == templateId).ExecuteDeleteAsync();
    }
}
