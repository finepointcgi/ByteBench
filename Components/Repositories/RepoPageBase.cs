using ByteBench.Data;
using ByteBench.Services;
using Microsoft.AspNetCore.Components;

namespace ByteBench.Components.Repositories;

/// <summary>Shared plumbing for pages under /p/{Key}/repos/{Slug}: loads the repository and commit-author lookup.</summary>
public abstract class RepoPageBase : ComponentBase
{
    [Inject] protected RepositoryService Repos { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected CurrentUser Me { get; set; } = null!;
    [Inject] protected RepositorySettingsService RepoSettings { get; set; } = null!;

    [Parameter] public string Key { get; set; } = "";
    [Parameter] public string Slug { get; set; } = "";

    protected Repository? Repo { get; private set; }
    protected bool NotFound { get; private set; }
    protected Dictionary<string, User> UsersByEmail { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    protected string? Error { get; set; }

    private string? loadedFor;

    /// <summary>Loads the repository once per Key/Slug. Returns false (and sets <see cref="NotFound"/>) if it doesn't exist.</summary>
    protected async Task<bool> LoadRepoAsync()
    {
        var id = $"{Key}/{Slug}".ToLowerInvariant();
        if (loadedFor != id)
        {
            Repo = await Repos.GetRepositoryAsync(Key, Slug);
            if (Repo == null && await RepoSettings.RedirectUrlAsync(Nav.Uri, Key, Slug) is { } moved)
            {
                // Renamed repository: send old URLs to the current slug.
                Nav.NavigateTo(moved, replace: true);
                return false;
            }
            NotFound = Repo == null;
            loadedFor = id;
            if (UsersByEmail.Count == 0) UsersByEmail = await Repos.GetUsersByEmailAsync();
        }
        return Repo != null;
    }

    /// <summary>Forces the next <see cref="LoadRepoAsync"/> to reload the repository row.</summary>
    protected void InvalidateRepo() => loadedFor = null;

    protected Task<T> Git<T>(Func<string, T> op) => Repos.Git(Repo!, op);
    protected Task Git(Action<string> op) => Repos.Git(Repo!, op);
}
