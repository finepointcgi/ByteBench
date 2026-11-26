using ByteBench.Data;

namespace ByteBench.Services.Ci;

public partial class PipelineService
{
    private static string RunDefaultsKey(int repositoryId) => $"ci:run-variables:{repositoryId}";

    /// <summary>
    /// The variables of the last "Run pipeline" done from the UI in this repository, as <c>NAME=value</c> lines, to prefill the next
    /// run. Not secret (run variables never are); empty without PushCode.
    /// </summary>
    public async Task<string> GetRunDefaultsAsync(Repository repo)
    {
        if (!await access.CanAsync(repo.ProjectId, Permission.PushCode)) return "";
        return await siteSettings.GetAsync(RunDefaultsKey(repo.Id)) ?? "";
    }

    /// <summary>Remembers the variables of a run (an empty set forgets them). Validated like the run itself. Needs PushCode.</summary>
    public async Task RememberRunVariablesAsync(Repository repo, IReadOnlyDictionary<string, string>? variables)
    {
        await access.RequireAsync(repo.ProjectId, Permission.PushCode);
        var vars = ValidateRunVariables(variables);
        var text = vars == null ? "" : string.Join("\n", vars.Select(kv => $"{kv.Key}={kv.Value}"));
        await siteSettings.SetAsync(RunDefaultsKey(repo.Id), text, await access.ResolveUserIdAsync());
    }
}
