using System.Runtime.CompilerServices;
using ByteBench.Data;
using ByteBench.Services.TrackerImport;

namespace ByteBench.Tests.Infrastructure;

/// <summary>
/// A source reader that serves canned data, for testing the import core (writer, runner, service) without any HTTP. Register it with
/// <c>services.AddSingleton&lt;ISourceReaderFactory&gt;(new CannedSourceFactory(...))</c> in a fixture's <c>ConfigureServices</c>.
/// </summary>
public sealed class CannedSourceFactory(CannedSource source) : ISourceReaderFactory
{
    public CannedSource Source { get; } = source;
    public ISourceReader Create(SourceConnection connection) => new CannedReader(Source, connection);
    public string? ValidateAddress(TrackerSourceKind kind, string? baseUrl) => null;
}

public sealed class CannedSource
{
    public List<SourceIssue> Issues { get; set; } = [];
    public List<SourceLabel> Labels { get; set; } = [];
    public List<SourceMilestone> Milestones { get; set; } = [];
    public List<SourceStatus> Statuses { get; set; } = [];
    public List<SourceSprint> Sprints { get; set; } = [];
    /// <summary>File bytes by attachment download address; an address not listed can't be fetched.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];
    public string? FailWith { get; set; }
    public int? AuthFailureAfter { get; set; }
    public SourceConnection? LastConnection { get; set; }
    /// <summary>Called after each issue is yielded (to cancel mid-run, for example).</summary>
    public Action<int>? OnYield { get; set; }
}

internal sealed class CannedReader(CannedSource s, SourceConnection connection) : ISourceReader
{
    public Task<SourceProbe> ProbeAsync(CancellationToken ct)
    {
        s.LastConnection = connection;
        if (s.FailWith != null) throw new SourceException(s.FailWith);
        return Task.FromResult(new SourceProbe("Canned project", null, s.Issues.Count, s.Issues.Take(3).Select(i => i.Title).ToList()));
    }
    public Task<IReadOnlyList<SourceStatus>> ReadStatusesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceStatus>>(s.Statuses);
    public Task<IReadOnlyList<SourceLabel>> ReadLabelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceLabel>>(s.Labels);
    public Task<IReadOnlyList<SourceMilestone>> ReadMilestonesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceMilestone>>(s.Milestones);
    public Task<IReadOnlyList<SourceSprint>> ReadSprintsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceSprint>>(s.Sprints);
    public Task<Stream> OpenAttachmentAsync(SourceAttachment attachment, CancellationToken ct, long maxBytes = TrackerHttp.DefaultAttachmentMaxBytes) =>
        attachment.DownloadUrl != null && s.Files.TryGetValue(attachment.DownloadUrl, out var bytes) ? Task.FromResult<Stream>(new MemoryStream(bytes)) : throw new SourceException("The source no longer has the file.");
    public async IAsyncEnumerable<SourceIssue> ReadIssuesAsync(SourceReadOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var n = 0;
        foreach (var i in s.Issues)
        {
            ct.ThrowIfCancellationRequested();
            if (s.AuthFailureAfter is { } a && n >= a) throw new SourceException("The source rejected the credentials.", authFailure: true);
            yield return i;
            n++;
            s.OnYield?.Invoke(n);
            await Task.Yield();
        }
    }
    public void Dispose() { }
}
