namespace ByteBench.Desktop.Core.Tests;

public class WorkspaceFramingTests
{
    private static readonly NavContext Local = new("http://localhost:5108");
    private static readonly NavContext Prod = new("https://bb.example.com", "example.com");

    [Theory]
    [InlineData("http://ide-ab12.localhost:5108/_bb/enter?ticket=t&next=/", WorkspaceKind.Ide)]
    [InlineData("http://p3000-ab12.localhost:5108/", WorkspaceKind.Preview)]
    [InlineData("http://localhost:5108/workspaces/ab12", WorkspaceKind.None)]
    [InlineData("http://ide-ab12.localhost:5109/", WorkspaceKind.None)]        // other port
    [InlineData("https://ide-ab12.localhost:5108/", WorkspaceKind.None)]       // other scheme
    [InlineData("http://ide-ab12.evil.test:5108/", WorkspaceKind.None)]
    [InlineData("http://a.ide-ab12.localhost:5108/", WorkspaceKind.None)]
    [InlineData("http://u:p@ide-ab12.localhost:5108/", WorkspaceKind.None)]
    [InlineData(null, WorkspaceKind.None)]
    [InlineData("not a url", WorkspaceKind.None)]
    public void Kind_of_local_urls(string? url, WorkspaceKind expected) => Assert.Equal(expected, NavigationPolicy.WorkspaceKindOf(url, Local));

    [Fact]
    public void Kind_uses_the_advertised_base()
    {
        Assert.Equal(WorkspaceKind.Ide, NavigationPolicy.WorkspaceKindOf("https://ide-x1.example.com/", Prod));
        Assert.Equal(WorkspaceKind.Preview, NavigationPolicy.WorkspaceKindOf("https://p8080-x1.bb.example.com/", Prod));
        Assert.Equal(WorkspaceKind.None, NavigationPolicy.WorkspaceKindOf("https://ide-x1.other.com/", Prod));
    }

    [Theory]
    [InlineData("http://ide-ab12.localhost:5108/_bb/enter?ticket=t", "http://localhost:5108/workspaces/ab12", true)]
    [InlineData("http://p3000-ab12.localhost:5108/", "http://localhost:5108/workspaces/ab12", true)]
    [InlineData("http://ide-ab12.localhost:5108/", "http://ide-ab12.localhost:5108/", false)]     // a frame inside the IDE page itself
    [InlineData("http://p3000-ab12.localhost:5108/", "http://ide-ab12.localhost:5108/", false)]
    [InlineData("http://localhost:5108/x", "http://localhost:5108/workspaces/ab12", false)]       // not a workspace origin
    [InlineData("https://evil.example.com/", "http://localhost:5108/workspaces/ab12", false)]
    [InlineData("http://ide-ab12.localhost:5108/", "https://evil.example.com/", false)]           // framed by another site: never ours to promote
    [InlineData("http://ide-ab12.localhost:5108/", null, false)]
    public void Frames_of_workspace_origins_inside_a_bytebench_page_are_promoted(string frame, string? top, bool expected) =>
        Assert.Equal(expected, NavigationPolicy.ShouldPromoteFrame(frame, top, Local));

    [Theory]
    [InlineData("http://localhost:5108/workspaces/ab12", "ab12")]
    [InlineData("http://localhost:5108/workspaces/ab12/", "ab12")]
    [InlineData("http://localhost:5108/workspaces", null)]
    [InlineData("http://localhost:5108/projects/workspaces/ab12", null)]
    [InlineData("http://ide-ab12.localhost:5108/workspaces/ab12", null)]
    [InlineData("https://evil.com/workspaces/ab12", null)]
    public void Workspace_id_from_page(string url, string? expected) => Assert.Equal(expected, NavigationPolicy.WorkspaceIdFromPage(url, Local));

    [Theory]
    [InlineData("My repo - ByteBench", "My repo")]
    [InlineData("Workspace acme/web | ByteBench", "Workspace acme/web")]
    [InlineData("ByteBench", "Workspace")]
    [InlineData("  ", "Workspace")]
    [InlineData(null, "Workspace")]
    [InlineData("Plain", "Plain")]
    public void Workspace_title(string? title, string expected) => Assert.Equal(expected, NavigationPolicy.WorkspaceTitle(title));

    [Fact]
    public void Workspace_title_is_truncated() => Assert.Equal(83, NavigationPolicy.WorkspaceTitle(new string('x', 200)).Length);

    private const string Page = "http://localhost:5108/workspaces/ab12";
    private const string Ide = "http://ide-ab12.localhost:5108/";

    [Fact]
    public void Back_from_the_ide_skips_the_page_that_framed_it() =>
        // [list, workspace page] then the IDE: Back goes to the list (2 entries back), not to the page that would re-open the IDE.
        Assert.Equal(2, NavigationPolicy.WorkspaceBackSteps(Ide, ["http://localhost:5108/workspaces", Page], Page));

    [Fact]
    public void Back_skips_in_ide_history_entries() =>
        Assert.Equal(3, NavigationPolicy.WorkspaceBackSteps(Ide + "?a=1", ["http://localhost:5108/", Page, Ide], Page));

    [Fact]
    public void Back_from_a_preview_returns_to_the_ide() =>
        Assert.Equal(1, NavigationPolicy.WorkspaceBackSteps("http://p3000-ab12.localhost:5108/", ["http://localhost:5108/", Page, Ide], Page));

    [Fact]
    public void Back_without_an_earlier_page_is_null()
    {
        Assert.Null(NavigationPolicy.WorkspaceBackSteps(Ide, [Page], Page));
        Assert.Null(NavigationPolicy.WorkspaceBackSteps(Ide, [], Page));
        Assert.Null(NavigationPolicy.WorkspaceBackSteps("garbage", ["http://localhost:5108/"], null));
    }

    [Fact]
    public void Back_without_a_framing_page_goes_one_step() =>
        Assert.Equal(1, NavigationPolicy.WorkspaceBackSteps(Ide, ["http://localhost:5108/other"], null));
}

public class DiagnosticsLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-diag-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("http://ide-ab.localhost:5108/_bb/enter?ticket=SECRET&next=/", "http://ide-ab.localhost:5108/_bb/enter?(query hidden)")]
    [InlineData("http://localhost:5108/workspaces/1#frag", "http://localhost:5108/workspaces/1")]
    [InlineData("http://u:pw@localhost:5108/x", "http://localhost:5108/x")]
    [InlineData(null, "(none)")]
    public void Redaction_never_keeps_query_fragment_or_userinfo(string? raw, string expected)
    {
        var r = DiagnosticsLog.RedactUrl(raw);
        Assert.Equal(expected, r);
        Assert.DoesNotContain("SECRET", r);
    }

    [Fact]
    public void Appends_lines_and_creates_the_folder()
    {
        var log = new DiagnosticsLog(Path.Combine(_dir, "logs", "webview.log"));
        log.Info("one");
        log.Error("two\nthree");
        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("INFO", lines[0]);
        Assert.EndsWith("two three", lines[1]);
    }

    [Fact]
    public void Rotates_past_the_size_limit()
    {
        var log = new DiagnosticsLog(Path.Combine(_dir, "w.log"), maxBytes: 200);
        for (var i = 0; i < 10; i++) log.Info(new string('x', 60));
        Assert.True(File.Exists(log.Path + ".1"));
        Assert.True(new FileInfo(log.Path).Length < 400);
    }

    [Fact]
    public void An_unwritable_path_does_not_throw()
    {
        var blocker = Path.Combine(_dir, "file");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "x");
        new DiagnosticsLog(Path.Combine(blocker, "sub", "w.log")).Info("ignored"); // parent is a file
    }
}
