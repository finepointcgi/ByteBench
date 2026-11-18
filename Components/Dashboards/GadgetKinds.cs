using GitBench.Data;
using MudBlazor;

namespace GitBench.Components.Dashboards;

/// <summary>Display metadata for each <see cref="GadgetKind"/>.</summary>
public static class GadgetKinds
{
    public sealed record Info(GadgetKind Kind, string Name, string Icon, string Description, int DefaultWidth);

    public static readonly IReadOnlyList<Info> All =
    [
        new(GadgetKind.FilterResults, "Filter results", Icons.Material.Outlined.FilterList, "Issues from a saved filter, with the columns you pick.", 8),
        new(GadgetKind.IssueChart, "Issue chart", Icons.Material.Outlined.PieChart, "Pie or bar chart of a filter's issues by status, assignee, priority, type or label.", 4),
        new(GadgetKind.CreatedVsResolved, "Created vs resolved", Icons.Material.Outlined.StackedLineChart, "Issues opened and closed per day over the last N days.", 6),
        new(GadgetKind.SprintBurndown, "Sprint burndown", Icons.Material.Outlined.TrendingDown, "The active (or a chosen) sprint's burndown.", 6),
        new(GadgetKind.ActivityStream, "Activity stream", Icons.Material.Outlined.History, "Recent issues, comments, pull requests and pushes.", 4),
        new(GadgetKind.AssignedToMe, "Assigned to me", Icons.Material.Outlined.AssignmentInd, "Your open issues, highest priority first.", 8),
        new(GadgetKind.ReviewRequests, "Awaiting my review", Icons.Material.Outlined.RateReview, "Open pull requests waiting for your review.", 6),
        new(GadgetKind.PipelineStatus, "Pipeline status", Icons.Material.Outlined.Bolt, "The latest CI pipelines of a repository.", 6),
        new(GadgetKind.Markdown, "Note", Icons.Material.Outlined.StickyNote2, "A Markdown note.", 4),
        new(GadgetKind.Projects, "Projects", Icons.Material.Outlined.Folder, "Your projects with open and closed issue counts.", 8),
    ];

    public static Info Of(GadgetKind k) => All.First(i => i.Kind == k);
}
