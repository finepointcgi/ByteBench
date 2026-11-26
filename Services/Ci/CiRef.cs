namespace ByteBench.Services.Ci;

/// <summary>
/// A pipeline's <see cref="Data.Pipeline.Ref"/>: a branch name (<c>main</c>), <c>refs/tags/{tag}</c> for a tag pipeline,
/// <c>refs/pull/{n}/head</c> for a fork pull request or <c>refs/merge-queue/…</c> for the merge queue.
/// </summary>
public static class CiRef
{
    public const string TagPrefix = "refs/tags/";

    /// <summary>The longest tag a pipeline can be created for: the ref column holds 250 characters.</summary>
    public const int MaxTagLength = 250 - 10;

    public static bool IsTag(string pipelineRef) => pipelineRef.StartsWith(TagPrefix, StringComparison.Ordinal);
    public static string TagName(string pipelineRef) => pipelineRef[TagPrefix.Length..];
    public static string ForTag(string tag) => TagPrefix + tag;

    /// <summary>What to show for a ref: the tag name for a tag pipeline, otherwise the ref as stored.</summary>
    public static string Display(string pipelineRef) => IsTag(pipelineRef) ? TagName(pipelineRef) : pipelineRef;

    /// <summary>A ref a person can open in the source browser: the tag name or the branch.</summary>
    public static string Browsable(string pipelineRef) => Display(pipelineRef);

    /// <summary>
    /// The predefined variables for a pipeline on <paramref name="pipelineRef"/> that rules and jobs see:
    /// <c>CI_COMMIT_REF_NAME</c> always, <c>CI_COMMIT_TAG</c> for tags, <c>CI_COMMIT_BRANCH</c> for branches (not for tags, fork
    /// pull requests or the merge queue) and <c>CI_PIPELINE_SOURCE</c>.
    /// </summary>
    public static Dictionary<string, string> Predefined(string pipelineRef, string source)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CI_COMMIT_REF_NAME"] = Display(pipelineRef),
            ["CI_PIPELINE_SOURCE"] = source,
        };
        if (IsTag(pipelineRef)) vars["CI_COMMIT_TAG"] = TagName(pipelineRef);
        else if (!pipelineRef.StartsWith("refs/", StringComparison.Ordinal)) vars["CI_COMMIT_BRANCH"] = pipelineRef;
        return vars;
    }
}
