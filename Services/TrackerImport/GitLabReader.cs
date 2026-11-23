using System.Runtime.CompilerServices;
using ByteBench.Data;

namespace ByteBench.Services.TrackerImport;

/// <summary>PLACEHOLDER: the GitLab reader is written by its own change; until then the source reports it isn't available.</summary>
public sealed class GitLabReader(SourceConnection connection, TrackerHttp http) : ISourceReader
{
    internal SourceConnection Connection { get; } = connection;
    internal TrackerHttp Http { get; } = http;
    private static SourceException NotYet() => new("The GitLab importer isn't available yet.");
    public Task<SourceProbe> ProbeAsync(CancellationToken ct) => throw NotYet();
    public Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct) => throw NotYet();
    public Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct) => throw NotYet();
    public Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct) => throw NotYet();
    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        throw NotYet();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
    public void Dispose() { }
}
