using ByteBench.Desktop.Core;

namespace ByteBench.Desktop.Core.Tests;

public class WorkspacePortsTests
{
    private const string Id = "abcdefghijklmnopqrst"; // 20 characters of [a-z2-7]: only letters here, all valid
    private const string Other = "bbbbbbbbbbbbbbbbbbbb";
    private static readonly NavContext Ctx = new("https://bb.example.com");

    private static string Entry(string label, string baseDomain = "bb.example.com", string query = "ticket=T1&next=%2F") =>
        $"https://{label}.{baseDomain}/_bb/enter?{query}";

    [Fact]
    public void Workspace_origins_are_read_with_their_workspace_and_port()
    {
        Assert.Equal(new WorkspaceOrigin(WorkspaceOriginKind.Preview, 5173, Id), NavigationPolicy.ParseWorkspaceOrigin($"https://p5173-{Id}.bb.example.com/", Ctx));
        Assert.Equal(new WorkspaceOrigin(WorkspaceOriginKind.Ide, null, Id), NavigationPolicy.ParseWorkspaceOrigin($"https://ide-{Id}.bb.example.com/x", Ctx));
    }

    [Theory]
    [InlineData("https://ide-abc.bb.example.com/")]                                  // not a real id
    [InlineData("https://p0-abcdefghijklmnopqrst.bb.example.com/")]
    [InlineData("https://p99999-abcdefghijklmnopqrst.bb.example.com/")]
    [InlineData("https://p5173-abcdefghijklmnopqrst.evil.example/")]
    [InlineData("https://x.p5173-abcdefghijklmnopqrst.bb.example.com/")]
    [InlineData("http://p5173-abcdefghijklmnopqrst.bb.example.com/")]
    [InlineData("https://p5173-abcdefghijklmnopqrst.bb.example.com:8443/")]
    [InlineData("https://bb.example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Look_alike_workspace_origins_are_not_workspaces(string? url) => Assert.Null(NavigationPolicy.ParseWorkspaceOrigin(url, Ctx));

    [Fact]
    public void A_localhost_development_server_keeps_its_port()
    {
        var local = new NavContext("http://localhost:5108");
        Assert.Equal(3000, NavigationPolicy.ParseWorkspaceOrigin($"http://p3000-{Id}.localhost:5108/", local)?.Port);
        Assert.Null(NavigationPolicy.ParseWorkspaceOrigin($"http://p3000-{Id}.localhost:5109/", local));
    }

    [Fact]
    public void Only_the_exact_single_use_entry_link_for_that_workspace_and_port_is_accepted()
    {
        Assert.True(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}"), Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Other}"), Ctx, Id, 5173)); // another workspace
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5174-{Id}"), Ctx, Id, 5173));    // another port
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"ide-{Id}"), Ctx, Id, 5173));      // the IDE
        Assert.False(NavigationPolicy.IsPortEntryUrl($"https://p5173-{Id}.bb.example.com/other?ticket=T&next=%2F", Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}", query: "ticket=T&next=%2F&x=1"), Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}", query: "ticket=T&ticket=U&next=%2F"), Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}", query: "ticket=T"), Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}", query: "ticket=&next=%2F"), Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}") + "#x", Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl($"https://u:p@p5173-{Id}.bb.example.com/_bb/enter?ticket=T&next=%2F", Ctx, Id, 5173));
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}", "evil.example"), Ctx, Id, 5173));
    }

    [Theory]
    [InlineData("https%3A%2F%2Fevil.example%2F")]
    [InlineData("%2F%2Fevil.example")]
    [InlineData("%5C%5Cevil")]
    [InlineData("foo")]
    public void A_next_target_that_leaves_the_origin_is_refused(string next) =>
        Assert.False(NavigationPolicy.IsPortEntryUrl(Entry($"p5173-{Id}", query: $"ticket=T&next={next}"), Ctx, Id, 5173));

    [Fact]
    public void The_entry_link_is_still_an_ordinary_workspace_url_for_the_navigation_policy()
    {
        Assert.Equal(NavDecision.Workspace, NavigationPolicy.Classify(Entry($"p5173-{Id}"), Ctx));
        Assert.Equal(NavDecision.External, NavigationPolicy.Classify(Entry($"p5173-{Id}", "evil.example"), Ctx));
    }

    [Fact]
    public void Running_workspaces_are_kept_with_valid_ids_only_and_their_text_is_cleaned()
    {
        var body = $$"""
            [
              {"id":"{{Id}}","project":"WEB","repository":"site","ref":"main\nINJECT","state":"Running"},
              {"id":"{{Other}}","project":"WEB","repository":"x","ref":"m","state":"Stopped"},
              {"id":"short","project":"WEB","repository":"x","ref":"m","state":"Running"},
              {"id":"AAAAAAAAAAAAAAAAAAAA","project":"WEB","repository":"x","ref":"m","state":"Running"},
              null, "x", 5
            ]
            """;
        var list = WorkspacePorts.ParseWorkspaces(body);
        Assert.Equal([new WorkspaceSummary(Id, "WEB/site", "main INJECT")], list);
        Assert.Empty(WorkspacePorts.ParseWorkspaces("""{"not":"an array"}"""));
        Assert.Empty(WorkspacePorts.ParseWorkspaces("not json"));
        Assert.Empty(WorkspacePorts.ParseWorkspaces(null));
    }

    [Fact]
    public void Ports_are_sorted_unique_and_an_origin_that_is_not_that_port_of_that_workspace_is_dropped()
    {
        var body = $$"""
            [
              {"port":8080,"origin":"https://p8080-{{Id}}.bb.example.com"},
              {"port":5173,"origin":"https://p5173-{{Other}}.bb.example.com"},
              {"port":5173},
              {"port":0},{"port":70000},{"port":1.5},{"port":"80"},null
            ]
            """;
        var ports = WorkspacePorts.ParsePorts(body, Ctx, Id);
        Assert.Equal([new ForwardedPort(5173, null), new ForwardedPort(8080, $"https://p8080-{Id}.bb.example.com")], ports);
        Assert.Empty(WorkspacePorts.ParsePorts("nope", Ctx, Id));
    }

    [Fact]
    public void The_open_response_is_accepted_only_when_its_url_is_the_entry_link_for_that_workspace_and_port()
    {
        var good = Entry($"p5173-{Id}");
        Assert.Equal(good, WorkspacePorts.ParseOpenResponse($$"""{"port":5173,"url":"{{good}}"}""", Ctx, Id, 5173));
        Assert.Null(WorkspacePorts.ParseOpenResponse($$"""{"url":"{{Entry($"p5173-{Other}")}}"}""", Ctx, Id, 5173));
        Assert.Null(WorkspacePorts.ParseOpenResponse("""{"url":"https://evil.example/"}""", Ctx, Id, 5173));
        Assert.Null(WorkspacePorts.ParseOpenResponse("""{"url":"file:///etc/passwd"}""", Ctx, Id, 5173));
        Assert.Null(WorkspacePorts.ParseOpenResponse("""{"url":5}""", Ctx, Id, 5173));
        Assert.Null(WorkspacePorts.ParseOpenResponse("null", Ctx, Id, 5173));
        Assert.Null(WorkspacePorts.ParseOpenResponse($$"""{"url":"{{good}}{{new string('x', 3000)}}"}""", Ctx, Id, 5173));
    }

    [Fact]
    public void Display_helpers_show_a_local_looking_address_and_tolerate_garbage()
    {
        Assert.Equal("p5173-abc.localhost:5108", WorkspacePorts.DisplayAddress("http://p5173-abc.localhost:5108"));
        Assert.Equal("", WorkspacePorts.DisplayAddress(null));
        Assert.Equal("", WorkspacePorts.DisplayAddress("not a url"));
        Assert.Contains("p5173-x.localhost:5108", WorkspacePorts.Label(new WorkspaceSummary(Id, "WEB/site", "main"), new ForwardedPort(5173, "http://p5173-x.localhost:5108")));
    }

    [Fact]
    public void The_server_must_advertise_the_capability()
    {
        Assert.True(WorkspacePorts.ServerSupportsPorts(["notifications", "workspace-ports"]));
        Assert.False(WorkspacePorts.ServerSupportsPorts(["notifications"]));
        Assert.False(WorkspacePorts.ServerSupportsPorts(null));
    }
}
