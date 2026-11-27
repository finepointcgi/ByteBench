namespace ByteBench.Desktop.Core.Tests;

public class ServerUrlTests
{
    [Theory]
    [InlineData("https://bytebench.example.com", "https://bytebench.example.com")]
    [InlineData("bytebench.example.com", "https://bytebench.example.com")]
    [InlineData("  Bytebench.Example.COM/some/path?q=1#frag  ", "https://bytebench.example.com")]
    [InlineData("localhost:5108", "http://localhost:5108")]
    [InlineData("http://192.168.1.20:8080/x", "http://192.168.1.20:8080")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("http://[::1]:5108", "http://[::1]:5108")]
    [InlineData("bytebench", "http://bytebench")]
    public void Normalize_accepts_and_canonicalizes(string input, string origin)
    {
        var r = ServerUrl.Normalize(input);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(origin, r.Origin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript://example.com/%0aalert(1)")]
    [InlineData("https://user:pw@example.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("file:///etc/passwd")]
    public void Normalize_rejects(string input)
    {
        var r = ServerUrl.Normalize(input);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void Plain_http_to_a_public_host_needs_confirmation()
    {
        var r = ServerUrl.Normalize("http://bytebench.example.com");
        Assert.False(r.Ok);
        Assert.True(r.NeedsHttpConfirmation);
        var confirmed = ServerUrl.Normalize("http://bytebench.example.com", allowInsecureRemote: true);
        Assert.True(confirmed.Ok);
        Assert.Equal("http://bytebench.example.com", confirmed.Origin);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("app.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.9", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("[::1]", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("printer.local", true)]
    [InlineData("intranet", true)]
    [InlineData("example.com", false)]
    public void IsPrivateHost(string host, bool expected) => Assert.Equal(expected, ServerUrl.IsPrivateHost(host));
}

public class NavigationPolicyTests
{
    private static readonly NavContext Ctx = new("https://bb.example.com", "example.com");

    [Theory]
    [InlineData("https://bb.example.com/p/WEB/issues/1", NavDecision.Internal)]
    [InlineData("https://BB.example.com/", NavDecision.Internal)]
    [InlineData("https://ide-abc123.bb.example.com/", NavDecision.Workspace)]
    [InlineData("https://p3000-abc123.example.com/", NavDecision.Workspace)]
    [InlineData("https://evil.example.com/", NavDecision.External)]            // sibling host that is not a workspace label
    [InlineData("https://ide-abc.evil.com/", NavDecision.External)]
    [InlineData("https://a.ide-abc.example.com/", NavDecision.External)]       // more than one label in front of the base
    [InlineData("http://bb.example.com/", NavDecision.External)]               // different scheme = different origin
    [InlineData("https://bb.example.com:8443/", NavDecision.External)]         // different port
    [InlineData("https://bb.example.com.evil.com/", NavDecision.External)]
    [InlineData("https://github.com/x", NavDecision.External)]
    [InlineData("mailto:a@b.c", NavDecision.External)]
    [InlineData("javascript:alert(1)", NavDecision.Blocked)]
    [InlineData("file:///etc/passwd", NavDecision.Blocked)]
    [InlineData("data:text/html,hi", NavDecision.Blocked)]
    [InlineData("bytebench://open?server=x", NavDecision.Blocked)]
    [InlineData("https://user:pw@bb.example.com/", NavDecision.Blocked)]
    [InlineData("/relative", NavDecision.Blocked)]
    [InlineData("", NavDecision.Blocked)]
    public void Classify(string url, NavDecision expected) => Assert.Equal(expected, NavigationPolicy.Classify(url, Ctx));

    [Fact]
    public void Advertised_base_must_be_a_parent_of_the_server_host()
    {
        // "com" is a public suffix and "other.org" is unrelated: neither widens the workspace set.
        Assert.Equal(NavDecision.External, NavigationPolicy.Classify("https://ide-x.com/", new("https://bb.example.com", "com")));
        Assert.Equal(NavDecision.External, NavigationPolicy.Classify("https://ide-x.other.org/", new("https://bb.example.com", "other.org")));
    }

    [Fact]
    public void Without_an_advertised_base_workspaces_live_under_the_server_host()
    {
        var ctx = new NavContext("https://bb.example.com");
        Assert.Equal(NavDecision.Workspace, NavigationPolicy.Classify("https://ide-abc.bb.example.com/", ctx));
        Assert.Equal(NavDecision.External, NavigationPolicy.Classify("https://ide-abc.example.com/", ctx));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("file:///x", false)]
    [InlineData("javascript:1", false)]
    [InlineData("https://u:p@example.com", false)]
    public void IsSafeExternal(string url, bool expected) => Assert.Equal(expected, NavigationPolicy.IsSafeExternal(url));
}
