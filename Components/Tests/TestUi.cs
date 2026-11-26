using ByteBench.Data;
using MudBlazor;

namespace ByteBench.Components.Tests;

/// <summary>Colors, icons and labels for test results, shared by the test pages and panels.</summary>
public static class TestUi
{
    public static Color ColorOf(TestResultStatus? s) => s switch
    {
        TestResultStatus.Passed => Color.Success,
        TestResultStatus.Failed => Color.Error,
        TestResultStatus.Blocked => Color.Warning,
        TestResultStatus.Skipped => Color.Info,
        _ => Color.Default,
    };

    public static string IconOf(TestResultStatus? s) => s switch
    {
        TestResultStatus.Passed => Icons.Material.Filled.CheckCircle,
        TestResultStatus.Failed => Icons.Material.Filled.Cancel,
        TestResultStatus.Blocked => Icons.Material.Filled.Block,
        TestResultStatus.Skipped => Icons.Material.Filled.SkipNext,
        _ => Icons.Material.Outlined.RadioButtonUnchecked,
    };

    public static string LabelOf(TestResultStatus? s) => s switch
    {
        TestResultStatus.Passed => "Passed",
        TestResultStatus.Failed => "Failed",
        TestResultStatus.Blocked => "Blocked",
        TestResultStatus.Skipped => "Skipped",
        TestResultStatus.NotRun => "Not run",
        _ => "—",
    };

    public static string CssOf(TestResultStatus? s) => s switch
    {
        TestResultStatus.Passed => "ts-pass",
        TestResultStatus.Failed => "ts-fail",
        TestResultStatus.Blocked => "ts-block",
        TestResultStatus.Skipped => "ts-skip",
        _ => "ts-none",
    };

    public static Color PriorityColor(TestPriority p) => p switch
    {
        TestPriority.Critical => Color.Error,
        TestPriority.High => Color.Warning,
        TestPriority.Low => Color.Default,
        _ => Color.Info,
    };

    public static string Cases(string key) => $"/p/{key}/tests";
    public static string Case(string key, int number) => $"/p/{key}/tests/cases/{number}";
    public static string Plans(string key) => $"/p/{key}/tests/plans";
    public static string Runs(string key) => $"/p/{key}/tests/runs";
    public static string Run(string key, int number) => $"/p/{key}/tests/runs/{number}";
    public static string Compare(string key, int? a = null, int? b = null) => $"/p/{key}/tests/compare" + (a != null && b != null ? $"?a={a}&b={b}" : "");
}
