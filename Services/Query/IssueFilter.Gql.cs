namespace GitBench.Services;

/// <summary>Advanced (GQL) search on the issue list: <c>?gql=…</c>. When set, the list runs the query instead of the basic filters.</summary>
public partial record IssueFilter
{
    public string? Gql { get; set; }
}
