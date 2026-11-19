using System.Text.Json;
using System.Text.Json.Serialization;
using GitBench.Data;

namespace GitBench.Services.Security;

/// <summary>
/// Night Owl's shared triage file, <c>.nightowl/triage.json</c> (its <c>TriageStore</c>): a JSON array of decisions,
/// camelCase, states in kebab-case, nulls left out, sorted by finding id. GitBench reads and writes the same shape, so
/// decisions made here can be committed to the repository (where <c>nightowl scan</c> in CI applies them) and decisions
/// committed there can be imported.
/// <code>
/// [ { "findingId": "3f2a…", "state": "accepted-risk", "reason": "Not reachable", "by": "alice",
///     "at": "2026-11-01T10:00:00+00:00", "expires": "2027-01-01T00:00:00+00:00", "title": "lodash prototype pollution" } ]
/// </code>
/// </summary>
public static class NightOwlTriageFile
{
    public const string RepositoryPath = ".nightowl/triage.json";

    public sealed record Decision
    {
        public required string FindingId { get; init; }
        public required string State { get; init; }
        public string? Reason { get; init; }
        public string? By { get; init; }
        public required DateTimeOffset At { get; init; }
        public DateTimeOffset? Expires { get; init; }
        public string? Title { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The file for <paramref name="decisions"/> (New, i.e. "reopened", is never written: it means no decision).</summary>
    public static string Write(IEnumerable<SecurityTriage> decisions) =>
        JsonSerializer.Serialize(decisions.Where(d => d.State != SecurityTriageState.New).OrderBy(d => d.Fingerprint, StringComparer.Ordinal).Select(d => new Decision
        {
            FindingId = d.Fingerprint,
            State = SecurityReportParser.TriageStateName(d.State),
            Reason = d.Reason,
            By = d.User?.UserName ?? d.By,
            At = new DateTimeOffset(DateTime.SpecifyKind(d.At, DateTimeKind.Utc)),
            Expires = d.ExpiresAt is { } e ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)) : null,
            Title = d.Title,
        }), Json) + "\n";

    /// <summary>Parsed decisions with their states.</summary>
    /// <exception cref="InvalidDataException">Not a JSON array of decisions, or an unknown state.</exception>
    public static List<(Decision Decision, SecurityTriageState State)> Read(string json)
    {
        List<Decision>? list;
        try { list = JsonSerializer.Deserialize<List<Decision>>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"Not a Night Owl triage file: {ex.Message}", ex); }
        if (list == null) throw new InvalidDataException("Not a Night Owl triage file: expected a JSON array.");
        var result = new List<(Decision, SecurityTriageState)>();
        foreach (var d in list)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.FindingId) || d.FindingId.Length > 64)
                throw new InvalidDataException("Every decision needs a findingId (at most 64 characters).");
            var state = SecurityReportParser.ParseTriageState(d.State)
                ?? throw new InvalidDataException($"Unknown triage state '{d.State}' for {d.FindingId} (use false-positive, accepted-risk, confirmed, fixed or new).");
            result.Add((d, state));
        }
        return result;
    }
}
