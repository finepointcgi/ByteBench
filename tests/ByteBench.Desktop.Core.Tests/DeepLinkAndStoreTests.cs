namespace ByteBench.Desktop.Core.Tests;

public class DeepLinkTests
{
    private static readonly ServerProfile A = new() { Id = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", Name = "Work", Origin = "https://bb.example.com" };
    private static readonly ServerProfile B = new() { Id = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", Name = "Home", Origin = "http://localhost:5108" };
    private static readonly IReadOnlyList<ServerProfile> Both = [A, B];

    [Fact]
    public void Opens_a_path_on_a_server_matched_by_name_host_origin_or_id()
    {
        foreach (var server in new[] { "Work", "work", "bb.example.com", "https://bb.example.com", A.Id })
        {
            var r = DeepLink.Parse($"bytebench://open?server={Uri.EscapeDataString(server)}&path=/p/KEY/issues/12", Both);
            Assert.True(r.Ok, $"{server}: {r.Reason}");
            Assert.Equal(A.Id, r.ProfileId);
            Assert.Equal("/p/KEY/issues/12", r.Path);
            Assert.Equal("https://bb.example.com/p/KEY/issues/12", r.Url);
        }
        Assert.Equal(B.Id, DeepLink.Parse("bytebench://open?server=localhost:5108&path=/", Both).ProfileId);
    }

    [Fact]
    public void Encoded_path_with_its_own_query_is_decoded_once()
    {
        var r = DeepLink.Parse("bytebench://open?server=Work&path=%2Fp%2FK%2Fissues%3Fstate%3Dopen%26x%3D1", Both);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("/p/K/issues?state=open&x=1", r.Path);
    }

    [Fact]
    public void Missing_path_means_the_server_root()
    {
        var r = DeepLink.Parse("bytebench://open?server=Work", Both);
        Assert.True(r.Ok);
        Assert.Equal("/", r.Path);
    }

    [Fact]
    public void Server_may_be_omitted_only_when_exactly_one_is_saved()
    {
        Assert.True(DeepLink.Parse("bytebench://open?path=/x", [A]).Ok);
        Assert.False(DeepLink.Parse("bytebench://open?path=/x", Both).Ok);
        Assert.False(DeepLink.Parse("bytebench://open?path=/x", []).Ok);
    }

    [Fact]
    public void A_link_can_never_add_an_unknown_server()
    {
        var r = DeepLink.Parse("bytebench://open?server=evil.example.org&path=/", Both);
        Assert.False(r.Ok);
        Assert.Contains("Add the server first", r.Reason);
        Assert.False(DeepLink.Parse("bytebench://open?server=https%3A%2F%2Fevil.example.org&path=/", Both).Ok);
        Assert.False(DeepLink.Parse("bytebench://add?server=https%3A%2F%2Fevil.example.org", Both).Ok);
    }

    [Theory]
    [InlineData("//evil.com/x")]
    [InlineData("/\\evil.com")]
    [InlineData("https://evil.com/")]
    [InlineData("p/KEY/issues")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/ok/../../x\r")]
    [InlineData("/with\\backslash")]
    [InlineData("/a\nb")]
    public void Paths_that_are_not_a_single_leading_slash_server_path_are_refused(string path)
    {
        var r = DeepLink.Parse($"bytebench://open?server=Work&path={Uri.EscapeDataString(path)}", Both);
        // "/ok/../../x%0d" is safe once decoded only if it had no control chars; the escaped %0d decodes to CR here.
        Assert.False(r.Ok, $"{path} was accepted as {r.Path}");
    }

    [Theory]
    [InlineData("https://bb.example.com/open?server=Work")]
    [InlineData("bytebench:open")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Other_schemes_and_junk_are_refused(string link) => Assert.False(DeepLink.Parse(link, Both).Ok);

    [Fact]
    public void Ambiguous_names_are_refused()
    {
        var twin = new ServerProfile { Id = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", Name = "work", Origin = "https://other.example.com" };
        var r = DeepLink.Parse("bytebench://open?server=work&path=/", [A, twin]);
        Assert.False(r.Ok);
        Assert.Contains("More than one", r.Reason);
    }

    [Fact]
    public void Overlong_links_are_refused() =>
        Assert.False(DeepLink.Parse("bytebench://open?server=Work&path=/" + new string('a', 5000), Both).Ok);

    [Fact]
    public void Extract_finds_a_link_among_arguments() =>
        Assert.Equal("bytebench://open?server=x", DeepLink.Extract(["app.exe", "--flag", "bytebench://open?server=x"]));

    [Theory]
    [InlineData("/p/KEY/issues/1", true)]
    [InlineData("/", true)]
    [InlineData("//x", false)]
    [InlineData("", false)]
    [InlineData("/x\ty", false)]
    public void IsSafePath(string path, bool expected) => Assert.Equal(expected, DeepLink.IsSafePath(path, "https://bb.example.com"));
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bb-desktop-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ProfileStoreTests
{
    [Fact]
    public void Add_persists_and_reloads()
    {
        using var dir = new TempDir();
        var file = dir.File("profiles.json");
        var store = new ProfileStore(file);
        var added = store.Add("Work", "bb.example.com");
        Assert.True(added.Ok, added.Error);
        Assert.Equal("https://bb.example.com", added.Value!.Origin);
        Assert.Matches("^[0-9a-f]{32}$", added.Value.Id);

        var reloaded = new ProfileStore(file);
        var p = Assert.Single(reloaded.List());
        Assert.Equal("Work", p.Name);
        Assert.Equal(added.Value.Id, p.Id);
    }

    [Fact]
    public void Name_defaults_to_the_host_and_duplicates_are_refused()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.File("p.json"));
        Assert.Equal("localhost:5108", store.Add(null, "localhost:5108").Value!.Name);
        var dup = store.Add("again", "http://localhost:5108/other");
        Assert.False(dup.Ok);
        Assert.Contains("already", dup.Error);
    }

    [Fact]
    public void Insecure_public_http_needs_confirmation_and_bad_urls_are_refused()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.File("p.json"));
        Assert.False(store.Add("x", "http://example.com").Ok);
        Assert.True(store.Add("x", "http://example.com", confirmInsecureHttp: true).Ok);
        Assert.False(store.Add("y", "ftp://example.com").Ok);
        Assert.Single(store.List());
    }

    [Fact]
    public void Rename_remove_and_last_opened()
    {
        using var dir = new TempDir();
        var file = dir.File("p.json");
        var store = new ProfileStore(file);
        var a = store.Add("A", "a.example.com").Value!;
        var b = store.Add("B", "b.example.com").Value!;
        Assert.Equal("Renamed", store.Rename(a.Id, "  Renamed\u0007 ").Value!.Name);
        store.MarkOpened(b.Id);
        Assert.Equal(b.Id, new ProfileStore(file).Settings().LastProfileId);
        Assert.True(store.Remove(b.Id));
        Assert.False(store.Remove(b.Id));
        Assert.Null(new ProfileStore(file).Settings().LastProfileId);
        Assert.False(store.Rename("nope", "x").Ok);
    }

    [Fact]
    public void LastNotified_never_decreases()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.File("p.json"));
        var id = store.Add("A", "a.example.com").Value!.Id;
        Assert.Null(store.Get(id)!.LastNotifiedId);
        store.SetLastNotified(id, 0);
        Assert.Equal(0, store.Get(id)!.LastNotifiedId);
        store.SetLastNotified(id, 7);
        store.SetLastNotified(id, 3);
        Assert.Equal(7, store.Get(id)!.LastNotifiedId);
    }

    [Fact]
    public void Corrupt_file_starts_empty_and_is_replaced_on_save()
    {
        using var dir = new TempDir();
        var file = dir.File("p.json");
        File.WriteAllText(file, "{ not json");
        var store = new ProfileStore(file);
        Assert.Empty(store.List());
        Assert.True(store.Add("A", "a.example.com").Ok);
        Assert.Single(new ProfileStore(file).List());
    }

    [Fact]
    public void Hand_edited_entries_that_fail_validation_are_dropped()
    {
        using var dir = new TempDir();
        var file = dir.File("p.json");
        File.WriteAllText(file, """
        {"version":1,"profiles":[
          {"id":"a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1","name":"ok","origin":"https://ok.example.com"},
          {"id":"not-a-guid","name":"bad id","origin":"https://x.example.com"},
          {"id":"b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2","name":"bad origin","origin":"javascript:alert(1)"},
          {"id":"c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3","name":"path in origin","origin":"https://x.example.com/evil"},
          {"id":"a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1","name":"dup id","origin":"https://dup.example.com"}
        ],"settings":{"lastProfileId":"gone"}}
        """);
        var store = new ProfileStore(file);
        Assert.Equal("ok", Assert.Single(store.List()).Name);
        Assert.Null(store.Settings().LastProfileId);
    }

    [Fact]
    public void File_holds_no_tokens_and_is_owner_only_on_unix()
    {
        using var dir = new TempDir();
        var file = dir.File("p.json");
        var store = new ProfileStore(file);
        store.Add("A", "a.example.com");
        Assert.DoesNotContain("token", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void Limit_of_profiles()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.File("p.json"));
        for (var i = 0; i < ProfileStore.MaxProfiles; i++) Assert.True(store.Add(null, $"h{i}.example.com").Ok);
        Assert.False(store.Add(null, "one-more.example.com").Ok);
    }

    [Fact]
    public async Task InMemoryTokenStore_roundtrip()
    {
        ITokenStore t = new InMemoryTokenStore();
        Assert.Null(await t.GetAsync("x"));
        Assert.False(await t.SetAsync("x", "   "));
        Assert.True(await t.SetAsync("x", " tok "));
        Assert.Equal("tok", await t.GetAsync("x"));
        await t.DeleteAsync("x");
        Assert.Null(await t.GetAsync("x"));
    }
}
