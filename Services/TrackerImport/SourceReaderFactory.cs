using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>Creates the reader for a source kind. Tests replace the registration to feed in canned data.</summary>
public interface ISourceReaderFactory
{
    ISourceReader Create(SourceConnection connection);
    /// <summary>Null if the address is acceptable for this source kind (empty is fine where the kind has a default), else why not.</summary>
    string? ValidateAddress(TrackerSourceKind kind, string? baseUrl);
}

public sealed class SourceReaderFactory(TrackerHttp http, TrackerUrlGuard guard) : ISourceReaderFactory
{
    public ISourceReader Create(SourceConnection c) => c.Kind switch
    {
        TrackerSourceKind.GitHub => new GitHubReader(c, http),
        TrackerSourceKind.GitLab => new GitLabReader(c, http),
        TrackerSourceKind.Jira => new JiraReader(c, http),
        _ => throw new SourceException("Unknown source."),
    };

    public string? ValidateAddress(TrackerSourceKind kind, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return kind == TrackerSourceKind.Jira ? "Enter your Jira address (for example https://example.atlassian.net)." : null;
        return guard.Validate(baseUrl);
    }
}
