using ByteBench.Data;
using ByteBench.Services.Git;

namespace ByteBench.Services.Ci;

/// <summary>One job as the linter shows it: after <c>extends</c>, <c>include</c>, matrix and stage needs have been applied.</summary>
public sealed record CiLintJob(string Name, string? Stage, string Image, IReadOnlyList<string> Needs, string When, bool AllowFailure,
    IReadOnlyList<string> Services, string? Coverage, string? ResourceGroup, string? Environment, string? OnStop);

/// <summary>The result of checking a pipeline file: precisely why it is invalid, or the jobs it would create.</summary>
public sealed record CiLintResult(bool Valid, string? Error, IReadOnlyList<string>? Stages, IReadOnlyList<CiLintJob> Jobs);

public partial class PipelineService
{
    /// <summary>
    /// Checks a pipeline file the way a run would parse it, without running anything. <paramref name="content"/> (up to 64 KB) is
    /// checked if given, otherwise the file at <paramref name="rev"/> (a branch, tag or commit; default: the default branch). Its
    /// <c>include:</c> files are read from the repository at <paramref name="rev"/> (remote ones only if the server allows them). The jobs
    /// shown are those that run on the default branch. Needs PushCode, because a remote include makes the server fetch an address.
    /// </summary>
    public async Task<CiLintResult> LintAsync(Repository repo, string? content, string? rev)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        var path = storage.PathFor(repo.Project.Key, repo.Slug);
        rev = string.IsNullOrWhiteSpace(rev) ? repo.DefaultBranch : rev.Trim();
        if (rev.Length > 250 || rev.StartsWith('-') || rev.Any(char.IsControl)) return new(false, "That is not a valid branch, tag or commit.", null, []);
        var parsed = await Task.Run(() =>
        {
            string? sha = null;
            try { sha = GitOps.ResolveSha(path, rev); } catch (Exception) { /* an empty repository: includes just won't be found */ }
            var text = content;
            if (text == null)
            {
                var blob = CiFile.GetBlob(path, sha ?? rev);
                if (blob == null) return new CiParseResult(null, $"There is no {CiFile.FileName} at the root of '{rev}'.");
                if (blob.TooLarge || blob.IsBinary || blob.Text == null) return new CiParseResult(null, $"{CiFile.FileName}: the file is too large or not text.");
                text = blob.Text;
            }
            return CiFile.Parse(text, triggers.IncludesFor(path, sha ?? rev));
        });
        if (parsed.Definition is not { } def) return new(false, parsed.Error, null, []);

        var (jobs, error) = def.ForBranch(repo.DefaultBranch);
        if (error != null) return new(false, error, def.Stages, []);
        return new(true, null, def.Stages, jobs.Select(j => new CiLintJob(j.Name, j.Stage, j.Image, j.Needs,
            j.When switch { CiJobWhen.Manual => "manual", CiJobWhen.Always => "always", CiJobWhen.OnFailure => "on_failure", CiJobWhen.Delayed => "delayed", _ => "on_success" },
            j.AllowFailure, j.Services.Select(s => s.Alias).ToList(), j.Coverage, j.ResourceGroup, j.Environment?.Name, j.Environment?.OnStop)).ToList());
    }
}
