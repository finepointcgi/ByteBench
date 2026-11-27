namespace ByteBench.Services.Workspaces;

/// <summary>What a start will do, decided before the container exists: the environment, the image to run and what to tell the user about it.</summary>
public sealed record WorkspaceProvisionPlan(WorkspaceEnvironment Env, IReadOnlyList<string> EnvErrors, string Image, string? BaseImage, string Summary, IReadOnlyList<string> Warnings);

public static class WorkspacePlanner
{
    /// <summary>
    /// Maps a repository's environment file to an image: a <c>toolchain</c> from the curated table (built on demand, cached), a repository-chosen
    /// <c>image</c> only when an administrator allowed that (else the default image and a visible note), or the default image. A file that
    /// didn't validate doesn't stop the workspace from opening: its problems are carried to the page.
    /// </summary>
    public static WorkspaceProvisionPlan Build(WorkspaceEnvLoad load, WorkspaceImage image, bool allowCustomImages)
    {
        var env = load.Environment;
        var errors = load.Result?.Errors ?? [];
        var warnings = new List<string>();
        if (load.Result != null) warnings.AddRange(load.Result.Notes);
        foreach (var e in errors) warnings.Add("Environment file: " + e);
        if (errors.Count > 0) warnings.Add($"{load.Result!.Source} was not used because of the problem(s) above; the workspace opened with the default setup. Fix the file and use \"Reload environment\".");

        var imageName = image.Image;
        string? baseImage = null;
        var summary = env.IsEmpty ? "default image" : $"default image ({env.Source})";
        if (env.Toolchain != null && WorkspaceToolchains.Resolve(env.Toolchain, out _) is { } tc)
        {
            imageName = image.TagFor(tc);
            baseImage = tc.BaseImage;
            summary = $"toolchain {env.Toolchain} ({env.Source})";
        }
        else if (env.Image != null)
        {
            if (allowCustomImages)
            {
                imageName = image.TagFor(env.Image);
                baseImage = env.Image;
                summary = $"image {env.Image} ({env.Source})";
            }
            else
            {
                warnings.Add($"{env.Source} asks for the base image '{env.Image}', but an administrator hasn't allowed images chosen by repositories (Admin → Workspaces). " +
                             "The default workspace image is used; pick a 'toolchain' instead (" + string.Join(", ", WorkspaceToolchains.All.Select(t => $"{t.Id}@{t.Version}")) + ").");
            }
        }
        return new WorkspaceProvisionPlan(env, errors, imageName, baseImage, summary.Length > 290 ? summary[..290] : summary, warnings);
    }
}
