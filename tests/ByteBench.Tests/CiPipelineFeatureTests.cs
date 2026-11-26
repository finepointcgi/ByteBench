using System.Net;
using System.Net.Sockets;
using System.Text;
using ByteBench.Data;
using ByteBench.Services;
using ByteBench.Services.Ci;
using ByteBench.Services.Webhooks;
using ByteBench.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary>An in-memory include source for <see cref="CiFile.Parse"/>.</summary>
internal sealed class FakeIncludes(Dictionary<string, string>? local = null, Dictionary<string, string>? remote = null) : ICiIncludeProvider
{
    public List<string> Reads { get; } = [];
    public CiIncludeRead ReadLocal(string path) { Reads.Add(path); return local != null && local.TryGetValue(path.TrimStart('/'), out var t) ? CiIncludeRead.Ok(t) : CiIncludeRead.Fail("was not found"); }
    public CiIncludeRead ReadRemote(string url) => remote != null && remote.TryGetValue(url, out var t) ? CiIncludeRead.Ok(t) : CiIncludeRead.Fail("was not found");
}

internal sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>stages, extends, anchors, include, services, coverage, when, resource_group, on_stop: the parser and the job graph (no Docker).</summary>
public class CiPipelineFeatureParseTests
{
    private static CiDefinition Ok(string yaml, ICiIncludeProvider? includes = null)
    {
        var r = CiFile.Parse(yaml, includes);
        Assert.True(r.Ok, r.Error);
        return r.Definition!;
    }

    private static string Err(string yaml, ICiIncludeProvider? includes = null)
    {
        var r = CiFile.Parse(yaml, includes);
        Assert.False(r.Ok, "expected a parse error");
        return r.Error!;
    }

    private static IReadOnlyList<CiJobDefinition> Run(CiDefinition d, string branch = "main")
    {
        var (jobs, error) = d.ForBranch(branch);
        Assert.Null(error);
        return jobs;
    }

    // ---------------------------------------------------------------- stages

    [Fact]
    public void Files_without_stages_behave_as_before()
    {
        var d = Ok("jobs:\n  a:\n    steps: [ { run: x } ]\n  b:\n    steps: [ { run: x } ]\n");
        Assert.Null(d.Stages);
        Assert.All(Run(d), j => { Assert.Empty(j.Needs); Assert.Null(j.Stage); });
    }

    [Fact]
    public void Stages_make_each_stage_wait_for_the_one_before_and_mix_with_needs()
    {
        var d = Ok("""
            stages: [build, test, deploy]
            jobs:
              compile:    { stage: build, steps: [ { run: x } ] }
              lint:       { stage: build, steps: [ { run: x } ] }
              unit:       { stage: test, steps: [ { run: x } ] }
              early:      { stage: test, needs: [], steps: [ { run: x } ] }
              only-lint:  { stage: test, needs: [lint], steps: [ { run: x } ] }
              ship:       { stage: deploy, steps: [ { run: x } ] }
            """);
        var jobs = Run(d).ToDictionary(j => j.Name);
        Assert.Equal(["compile", "lint"], jobs["unit"].Needs.Order());   // implicit: the whole previous stage
        Assert.Empty(jobs["early"].Needs);                               // needs: [] starts at once
        Assert.Equal(["lint"], jobs["only-lint"].Needs);                 // explicit needs are kept as written
        Assert.Equal(["early", "only-lint", "unit"], jobs["ship"].Needs.Order());
        Assert.Empty(jobs["compile"].Needs);
    }

    [Fact]
    public void Default_stages_and_the_default_stage_of_a_job_are_build_test_deploy_and_test()
    {
        var d = Ok("jobs:\n  a:\n    stage: build\n    steps: [ { run: x } ]\n  b:\n    steps: [ { run: x } ]\n  c:\n    stage: deploy\n    steps: [ { run: x } ]\n");
        Assert.Equal(["build", "test", "deploy"], d.Stages);
        var jobs = Run(d).ToDictionary(j => j.Name);
        Assert.Equal("test", jobs["b"].Stage);
        Assert.Equal(["a"], jobs["b"].Needs);
        Assert.Equal(["b"], jobs["c"].Needs);
    }

    [Fact]
    public void A_stage_skips_empty_stages_and_optional_manual_jobs_do_not_hold_the_next_stage_up()
    {
        var d = Ok("""
            stages: [one, two, three, four]
            jobs:
              a: { stage: one, steps: [ { run: x } ] }
              opt: { stage: three, when: manual, steps: [ { run: x } ] }
              last: { stage: four, steps: [ { run: x } ] }
            """);
        var jobs = Run(d).ToDictionary(j => j.Name);
        Assert.Equal(["a"], jobs["opt"].Needs);     // stage two is empty
        Assert.Equal(["a"], jobs["last"].Needs);    // stage three only has an optional manual job (allow_failure defaults to true)
    }

    [Fact]
    public void Rules_that_leave_out_a_whole_stage_leave_the_next_stage_depending_on_the_one_before()
    {
        var d = Ok("""
            stages: [build, test, deploy]
            jobs:
              b: { stage: build, steps: [ { run: x } ] }
              t: { stage: test, only: [other], steps: [ { run: x } ] }
              d: { stage: deploy, steps: [ { run: x } ] }
            """);
        Assert.Equal(["b"], Run(d).Single(j => j.Name == "d").Needs);
        Assert.Equal(["t"], Run(d, "other").Single(j => j.Name == "d").Needs);
    }

    [Theory]
    [InlineData("stages: [a, a]\njobs:\n  x: { stage: a, steps: [ { run: x } ] }\n", "twice")]
    [InlineData("stages: []\njobs:\n  x: { steps: [ { run: x } ] }\n", "at least one")]
    [InlineData("stages: [build]\njobs:\n  x: { stage: nope, steps: [ { run: x } ] }\n", "not in 'stages'")]
    [InlineData("stages: [build]\njobs:\n  x: { steps: [ { run: x } ] }\n", "default stage 'test'")]
    [InlineData("stages: [\"bad name\"]\njobs:\n  x: { stage: build, steps: [ { run: x } ] }\n", "invalid")]
    public void Bad_stages_are_reported_precisely(string yaml, string expected) => Assert.Contains(expected, Err(yaml));

    [Fact]
    public void A_needs_that_points_forward_in_the_stages_is_a_cycle()
    {
        var e = Err("stages: [build, test]\njobs:\n  a: { stage: build, needs: [b], steps: [ { run: x } ] }\n  b: { stage: test, steps: [ { run: x } ] }\n");
        Assert.Contains("cycle", e);
    }

    // ---------------------------------------------------------------- extends, hidden jobs, anchors

    [Fact]
    public void Extends_merges_hashes_replaces_lists_and_hidden_jobs_never_run()
    {
        var d = Ok("""
            jobs:
              .base:
                image: alpine:3.19
                variables: { A: "1", B: "2" }
                retry: 2
                steps: [ { run: base } ]
              .more:
                variables: { B: "later" }
                artifacts: { paths: [out/] }
              test:
                extends: [.base, .more]
                variables: { C: "3" }
                steps: [ { run: mine } ]
            """);
        var j = Assert.Single(d.Jobs);
        Assert.Equal("test", j.Name);
        Assert.Equal("alpine:3.19", j.Image);
        Assert.Equal(2, j.Retry);
        Assert.Equal("mine", j.Steps.Single().Run);                                   // steps are replaced
        Assert.Equal("1", j.Variables["A"]);
        Assert.Equal("later", j.Variables["B"]);                                      // later parent wins
        Assert.Equal("3", j.Variables["C"]);
        Assert.Equal(["out/"], j.Artifacts!.Paths);
    }

    [Fact]
    public void Extends_works_through_several_levels_and_from_a_visible_job()
    {
        var d = Ok("""
            jobs:
              .a: { image: alpine:3.18, steps: [ { run: a } ] }
              .b: { extends: .a, variables: { X: "1" } }
              real: { extends: .b }
              copy: { extends: real, steps: [ { run: other } ] }
            """);
        var jobs = d.Jobs.ToDictionary(j => j.Name);
        Assert.Equal(["copy", "real"], jobs.Keys.Order());
        Assert.Equal("alpine:3.18", jobs["real"].Image);
        Assert.Equal("1", jobs["copy"].Variables["X"]);
        Assert.Equal("other", jobs["copy"].Steps.Single().Run);
    }

    [Theory]
    [InlineData("jobs:\n  a: { extends: .nope, steps: [ { run: x } ] }\n", "extends '.nope', which is not defined")]
    [InlineData("jobs:\n  .a: { extends: .b }\n  .b: { extends: .a }\n  c: { extends: .a, steps: [ { run: x } ] }\n", "cycle")]
    [InlineData("jobs:\n  a: { extends: { x: 1 }, steps: [ { run: x } ] }\n", "'extends' must be")]
    [InlineData("jobs:\n  .t: { variables: { A: 1 } }\n", "at least one job")]
    public void Bad_extends_are_reported_precisely(string yaml, string expected) => Assert.Contains(expected, Err(yaml));

    [Fact]
    public void Yaml_anchors_aliases_and_merge_keys_work_in_jobs()
    {
        var d = Ok("""
            jobs:
              .defaults: &defaults
                image: alpine:3.19
                variables: { MODE: ci }
                steps: [ { run: echo hi } ]
              one:
                <<: *defaults
              two:
                <<: *defaults
                variables: { MODE: two }
            """);
        Assert.Equal(["one", "two"], d.Jobs.Select(j => j.Name));
        Assert.All(d.Jobs, j => Assert.Equal("alpine:3.19", j.Image));
        Assert.Equal("ci", d.Jobs[0].Variables["MODE"]);
        Assert.Equal("two", d.Jobs[1].Variables["MODE"]);
    }

    // ---------------------------------------------------------------- include

    [Fact]
    public void Include_local_files_merges_jobs_variables_and_the_files_own_jobs_win()
    {
        var inc = new FakeIncludes(new()
        {
            ["ci/common.yml"] = "image: alpine:3.17\nvariables: { SHARED: from-include, ONLY_INC: yes }\njobs:\n  lint:\n    steps: [ { run: lint } ]\n  build:\n    steps: [ { run: included-build } ]\n",
        });
        var d = Ok("include: [ { local: ci/common.yml } ]\nvariables: { SHARED: mine }\njobs:\n  build:\n    steps: [ { run: own-build } ]\n", inc);
        var jobs = d.Jobs.ToDictionary(j => j.Name);
        Assert.Equal("own-build", jobs["build"].Steps.Single().Run);
        Assert.Equal("lint", jobs["lint"].Steps.Single().Run);
        Assert.Equal("alpine:3.17", jobs["lint"].Image);                       // the included file's default image
        Assert.Equal("mine", jobs["lint"].Variables["SHARED"]);
        Assert.Equal("yes", jobs["lint"].Variables["ONLY_INC"]);
    }

    [Fact]
    public void Included_hidden_jobs_can_be_extended_and_a_later_include_replaces_an_earlier_one()
    {
        var inc = new FakeIncludes(new()
        {
            ["a.yml"] = "jobs:\n  .tpl:\n    image: alpine:3.16\n    steps: [ { run: tpl } ]\n  shared:\n    steps: [ { run: from-a } ]\n",
            ["b.yml"] = "jobs:\n  shared:\n    steps: [ { run: from-b } ]\n",
        });
        var d = Ok("include: [a.yml, /b.yml]\njobs:\n  mine:\n    extends: .tpl\n", inc);
        var jobs = d.Jobs.ToDictionary(j => j.Name);
        Assert.Equal("alpine:3.16", jobs["mine"].Image);
        Assert.Equal("from-b", jobs["shared"].Steps.Single().Run);
    }

    [Fact]
    public void Include_accepts_templates_by_name_and_as_a_mapping_and_files_in_a_file()
    {
        Assert.Contains(Ok("include: nightowl\n").Jobs, j => j.Name == "nightowl");
        Assert.Contains(Ok("include: [ { template: nightowl } ]\n").Jobs, j => j.Name == "nightowl");
        var inc = new FakeIncludes(new() { ["a.yml"] = "include: b.yml\njobs:\n  a: { steps: [ { run: a } ] }\n", ["b.yml"] = "jobs:\n  b: { steps: [ { run: b } ] }\n" });
        Assert.Equal(["a", "b"], Ok("include: a.yml\n", inc).Jobs.Select(j => j.Name).Order());
    }

    [Fact]
    public void Include_cycles_depth_missing_files_and_unknown_templates_are_errors()
    {
        var cyc = new FakeIncludes(new() { ["a.yml"] = "include: b.yml\njobs:\n  a: { steps: [ { run: a } ] }\n", ["b.yml"] = "include: a.yml\njobs:\n  b: { steps: [ { run: b } ] }\n" });
        Assert.Contains("cycle", Err("include: a.yml\n", cyc));
        Assert.Contains("a.yml → b.yml → a.yml", Err("include: a.yml\n", cyc));

        var deep = new Dictionary<string, string>();
        for (var i = 0; i < 10; i++) deep[$"f{i}.yml"] = $"include: f{i + 1}.yml\njobs:\n  j{i}: {{ steps: [ {{ run: x }} ] }}\n";
        deep["f10.yml"] = "jobs:\n  j10: { steps: [ { run: x } ] }\n";
        Assert.Contains("levels deep", Err("include: f0.yml\n", new FakeIncludes(deep)));

        Assert.Contains("was not found", Err("include: missing.yml\n", new FakeIncludes()));
        Assert.Contains("is not a built-in template", Err("include: nope\n"));
        Assert.Contains("files can't be included here", Err("include: ci/x.yml\n"));      // no provider (parse without a repository)
        Assert.Contains("unknown key", Err("include: [ { git: x } ]\n", new FakeIncludes()));
    }

    [Fact]
    public void Include_is_capped_by_file_count_and_by_total_size()
    {
        var many = new Dictionary<string, string>();
        for (var i = 0; i < 25; i++) many[$"f{i}.yml"] = $"jobs:\n  j{i}: {{ steps: [ {{ run: x }} ] }}\n";
        Assert.Contains("Too many included files", Err("include: [" + string.Join(", ", many.Keys) + "]\n", new FakeIncludes(many)));

        var big = new Dictionary<string, string>();
        var filler = new string('x', 60_000);
        for (var i = 0; i < 6; i++) big[$"b{i}.yml"] = $"# {filler}\njobs:\n  j{i}: {{ steps: [ {{ run: x }} ] }}\n";
        Assert.Contains("together", Err("include: [" + string.Join(", ", big.Keys) + "]\n", new FakeIncludes(big)));
    }

    [Fact]
    public void Invalid_yaml_in_an_included_file_names_the_file()
    {
        var inc = new FakeIncludes(new() { ["bad.yml"] = "jobs:\n  a:\n    stepz: 1\n" });
        var e = Err("include: bad.yml\n", inc);
        Assert.Contains("include of local 'bad.yml'", e);
        Assert.Contains("stepz", e);
    }

    // ---------------------------------------------------------------- local includes from a real repository

    [Fact]
    public void The_repository_include_provider_reads_the_commits_files_and_refuses_paths_outside()
    {
        var provider = new CiIncludeProvider("/nonexistent", "HEAD", new CiRemoteIncludes(new FixedMonitor<CiOptions>(new()), new FixedMonitor<WebhookOptions>(new())));
        foreach (var bad in new[] { "../secret.yml", "a/../../b.yml", "a\\b.yml", ".git/config", "", "a//b.yml" })
            Assert.NotNull(provider.ReadLocal(bad).Error);
    }

    // ---------------------------------------------------------------- remote includes

    private sealed class TinyServer : IDisposable
    {
        private readonly HttpListener listener = new();
        public string Base { get; }
        public int Hits;
        public TinyServer(Func<string, (int Status, string Body, string? Location)> handler)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Base + "/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await listener.GetContextAsync(); } catch { return; }
                    Interlocked.Increment(ref Hits);
                    var (status, body, location) = handler(c.Request.Url!.AbsolutePath);
                    c.Response.StatusCode = status;
                    if (location != null) c.Response.Headers["Location"] = location;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    try { await c.Response.OutputStream.WriteAsync(bytes); } catch { }
                    c.Response.Close();
                }
            });
        }
        public void Dispose() { try { listener.Close(); } catch { } }
    }

    private static CiRemoteIncludes Remote(bool enabled, bool? allowPrivate = null, string[]? hosts = null) =>
        new(new FixedMonitor<CiOptions>(new() { AllowRemoteIncludes = enabled, AllowPrivateNetworks = allowPrivate, RemoteIncludeAllowedHosts = hosts }),
            new FixedMonitor<WebhookOptions>(new() { AllowPrivateNetworks = false }));

    [Fact]
    public void Remote_includes_are_off_by_default()
    {
        var read = Remote(enabled: false).Fetch("https://example.com/ci.yml");
        Assert.Null(read.Text);
        Assert.Contains("turned off", read.Error);
        var parsed = Err("include: [ { remote: 'https://example.com/ci.yml' } ]\n", new CiIncludeProvider("/nonexistent", "HEAD", Remote(false)));
        Assert.Contains("turned off on this server", parsed);
    }

    [Theory]
    [InlineData("http://example.com/ci.yml", "https")]                       // plain http
    [InlineData("https://user:pw@example.com/ci.yml", "credentials")]
    [InlineData("https://127.0.0.1/ci.yml", "local")]
    [InlineData("https://localhost/ci.yml", "local")]
    [InlineData("https://10.0.0.5/ci.yml", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private")]
    [InlineData("https://[::1]/ci.yml", "local")]
    [InlineData("https://[fd00::1]/ci.yml", "private")]
    [InlineData("ftp://example.com/ci.yml", "https")]
    public void Remote_includes_refuse_local_private_and_odd_addresses(string url, string reason)
    {
        var read = Remote(enabled: true).Fetch(url);
        Assert.Null(read.Text);
        Assert.Contains(reason, read.Error);
    }

    [Fact]
    public void Remote_includes_can_be_limited_to_hosts()
    {
        var read = Remote(enabled: true, hosts: ["templates.example.org"]).Fetch("https://evil.example.com/ci.yml");
        Assert.Contains("RemoteIncludeAllowedHosts", read.Error);
    }

    [Fact]
    public void Remote_includes_fetch_when_allowed_cap_size_refuse_redirects_and_cache()
    {
        using var server = new TinyServer(path => path switch
        {
            "/ok.yml" => (200, "jobs:\n  remote-job:\n    steps: [ { run: from-remote } ]\n", null),
            "/big.yml" => (200, new string('#', 300_000), null),
            "/redir.yml" => (302, "", "http://127.0.0.1:1/x"),
            _ => (404, "", null),
        });
        var remote = Remote(enabled: true, allowPrivate: true);
        var provider = new CiIncludeProvider("/nonexistent", "HEAD", remote);
        var d = Ok($"include: [ {{ remote: '{server.Base}/ok.yml' }} ]\n", provider);
        Assert.Equal("from-remote", d.Jobs.Single().Steps.Single().Run);
        Ok($"include: [ {{ remote: '{server.Base}/ok.yml' }} ]\n", provider);
        Assert.Equal(1, server.Hits);                                                          // answers are cached

        Assert.Contains("larger than", remote.Fetch(server.Base + "/big.yml").Error);
        Assert.Contains("redirect", remote.Fetch(server.Base + "/redir.yml").Error);
        Assert.Contains("HTTP 404", remote.Fetch(server.Base + "/nope.yml").Error);
    }

    [Fact]
    public void Remote_includes_with_private_networks_off_never_connect_to_a_local_server()
    {
        using var server = new TinyServer(_ => (200, "jobs:\n  x: { steps: [ { run: x } ] }\n", null));
        var remote = Remote(enabled: true, allowPrivate: false);
        var read = remote.Fetch(server.Base.Replace("http://", "https://") + "/ok.yml");
        Assert.Null(read.Text);
        Assert.Equal(0, server.Hits);
    }

    // ---------------------------------------------------------------- services, coverage, when, resource_group, on_stop

    [Fact]
    public void Services_parse_with_aliases_variables_commands_and_ports()
    {
        var d = Ok("""
            jobs:
              test:
                services:
                  - postgres:17
                  - { image: "registry.example.com/team/redis:7", alias: cache, command: [redis-server, --appendonly, "no"] }
                  - { image: mysql:8, variables: { MYSQL_ROOT_PASSWORD: pw }, port: 3306 }
                steps: [ { run: x } ]
            """);
        var s = d.Jobs.Single().Services;
        Assert.Equal(["postgres", "cache", "mysql"], s.Select(x => x.Alias));
        Assert.Equal(["redis-server", "--appendonly", "no"], s[1].Command);
        Assert.Equal("pw", s[2].Variables!["MYSQL_ROOT_PASSWORD"]);
        Assert.Equal(3306, s[2].Port);
        Assert.Equal("postgres", CiFile.DefaultAlias("ghcr.io/x/postgres:17@sha256:abc"));
    }

    [Theory]
    [InlineData("services: [ postgres:17, { image: postgres:16 } ]", "alias")]
    [InlineData("services: [ { image: x, wat: 1 } ]", "unknown key")]
    [InlineData("services: [ { alias: x } ]", "valid image")]
    [InlineData("services: [ '-rm' ]", "valid image")]
    [InlineData("services: [ { image: x, alias: 'Bad Alias' } ]", "alias")]
    [InlineData("services: [ { image: x, port: 70000 } ]", "port")]
    [InlineData("services: [ { image: x, variables: { CI: 1 } } ]", "reserved")]
    [InlineData("services: [ a, b, c, d, e, f ]", "too many services")]
    [InlineData("services: [ [x] ]", "image name")]
    public void Bad_services_are_reported_precisely(string line, string expected) =>
        Assert.Contains(expected, Err($"jobs:\n  t:\n    {line}\n    steps: [ {{ run: x }} ]\n"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Coverage_patterns_are_checked_and_stored_without_slashes()
    {
        var d = Ok("jobs:\n  t:\n    coverage: '/Total.*?(\\d+\\.\\d+)%/'\n    steps: [ { run: x } ]\n");
        Assert.Equal(@"Total.*?(\d+\.\d+)%", d.Jobs.Single().Coverage);
        Assert.Contains("usable", Err("jobs:\n  t:\n    coverage: '/(?<=a)b/'\n    steps: [ { run: x } ]\n"));     // lookbehind: not supported (no backtracking engine)
        Assert.Contains("usable", Err("jobs:\n  t:\n    coverage: '/(a)\\1/'\n    steps: [ { run: x } ]\n"));      // backreference
        Assert.Contains("needs a regular expression", Err("jobs:\n  t:\n    coverage: '//'\n    steps: [ { run: x } ]\n"));
        Assert.Contains("longer than", Err($"jobs:\n  t:\n    coverage: '{new string('a', 300)}'\n    steps: [ {{ run: x }} ]\n"));
    }

    [Fact]
    public void Coverage_is_read_from_logs_cobertura_and_lcov()
    {
        var log = "Ran 12 tests\nTotal coverage: 71.5%\nDone\nTotal coverage: 83.25%\n";
        Assert.Equal(83.25, CiCoverage.FromLog(@"Total coverage: (\d+\.?\d*)%", log));        // the last match
        Assert.Equal(90, CiCoverage.FromLog(@"coverage \d+%", "coverage 90%"));               // no group: the number of the match
        Assert.Null(CiCoverage.FromLog(@"nothing (\d+)%", log));
        Assert.Null(CiCoverage.FromLog(@"Total coverage: (\d+\.?\d*)%", "Total coverage: 140%"));   // not a percentage

        Assert.Equal(80, CiCoverage.FromCobertura("<coverage line-rate=\"0.8\" lines-valid=\"10\" lines-covered=\"8\"><packages/></coverage>"));
        Assert.Equal(62.5, CiCoverage.FromCobertura("<coverage line-rate=\"0.625\"></coverage>"));
        Assert.Null(CiCoverage.FromCobertura("<report/>"));
        Assert.Null(CiCoverage.FromCobertura("<!DOCTYPE coverage [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><coverage line-rate=\"0.5\">&x;</coverage>"));
        Assert.Null(CiCoverage.FromCobertura("not xml"));

        Assert.Equal(75, CiCoverage.FromLcov("SF:a.c\nDA:1,1\nDA:2,0\nLF:2\nLH:1\nend_of_record\nSF:b.c\nLF:2\nLH:2\nend_of_record\n"));
        Assert.Equal(50, CiCoverage.FromLcov("SF:a.c\nDA:1,3\nDA:2,0\nend_of_record\n"));
        Assert.Null(CiCoverage.FromLcov(""));
        Assert.Equal(70, CiCoverage.Average([60, null, 80]));
        Assert.Null(CiCoverage.Average([null, null]));
    }

    [Fact]
    public void A_catastrophic_looking_coverage_pattern_cannot_hang_the_server()
    {
        var pattern = CiCoverage.NormalizePattern("/(a+)+$/").Pattern!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CiCoverage.FromLog(pattern, new string('a', 50_000) + "!");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");        // linear-time engine
    }

    [Fact]
    public void Cobertura_and_lcov_are_report_types_that_name_one_file()
    {
        var d = Ok("jobs:\n  t:\n    artifacts: { reports: { cobertura: cov/cobertura.xml, lcov: cov/lcov.info } }\n    steps: [ { run: x } ]\n");
        Assert.Equal("cov/cobertura.xml", d.Jobs.Single().Artifacts!.Reports!["cobertura"]);
        Assert.Contains("one file", Err("jobs:\n  t:\n    artifacts: { reports: { lcov: 'cov/*.info' } }\n    steps: [ { run: x } ]\n"));
    }

    [Fact]
    public void When_always_on_failure_and_delayed_parse_with_start_in()
    {
        var d = Ok("""
            jobs:
              cleanup: { when: always, steps: [ { run: x } ] }
              report:  { when: on_failure, steps: [ { run: x } ] }
              slow:    { when: delayed, start_in: 90m, steps: [ { run: x } ] }
              viarule:
                rules: [ { branches: [main], when: delayed }, { when: never } ]
                start_in: 5m
                steps: [ { run: x } ]
            """);
        var jobs = d.Jobs.ToDictionary(j => j.Name);
        Assert.Equal(CiJobWhen.Always, jobs["cleanup"].When);
        Assert.Equal(CiJobWhen.OnFailure, jobs["report"].When);
        Assert.Equal(TimeSpan.FromMinutes(90), jobs["slow"].StartIn);
        Assert.Equal(CiJobWhen.Delayed, Run(d).Single(j => j.Name == "viarule").When);
        Assert.False(jobs["slow"].AllowFailure);                       // only manual jobs default to allow_failure
        var extras = CiJobExtras.Parse(CiJobExtras.Serialize(Run(d).Single(j => j.Name == "slow")));
        Assert.True(extras.IsDelayed);
        Assert.Equal(CiJobTrigger.Always, CiJobExtras.Parse(CiJobExtras.Serialize(jobs["cleanup"])).Trigger);
    }

    [Theory]
    [InlineData("when: delayed", "needs 'start_in'")]
    [InlineData("when: delayed\n    start_in: soon", "not valid")]
    [InlineData("when: delayed\n    start_in: 3d", "up to 24h")]
    [InlineData("start_in: 5m", "only applies to 'when: delayed'")]
    [InlineData("when: sometimes", "always, on_failure or delayed")]
    [InlineData("resource_group: '../x'", "resource_group")]
    public void Bad_when_start_in_and_resource_group_are_reported(string lines, string expected) =>
        Assert.Contains(expected, Err($"jobs:\n  t:\n    {lines}\n    steps: [ {{ run: x }} ]\n"));

    [Fact]
    public void Resource_group_and_on_stop_are_validated_and_stored()
    {
        var d = Ok("""
            jobs:
              deploy:
                resource_group: production
                environment: { name: review/x, on_stop: stop-review }
                steps: [ { run: x } ]
              stop-review:
                when: manual
                environment: { name: review/x, action: stop }
                steps: [ { run: x } ]
            """);
        var deploy = d.Jobs.Single(j => j.Name == "deploy");
        Assert.Equal("production", deploy.ResourceGroup);
        var extras = CiJobExtras.Parse(CiJobExtras.Serialize(deploy));
        Assert.Equal("production", extras.ResourceGroup);
        Assert.Equal("stop-review", extras.OnStop);

        Assert.Contains("is not a job in this file", Err("jobs:\n  d:\n    environment: { name: e, on_stop: gone }\n    steps: [ { run: x } ]\n"));
        Assert.Contains("action: stop", Err("jobs:\n  d:\n    environment: { name: e, on_stop: s }\n    steps: [ { run: x } ]\n  s:\n    environment: { name: other }\n    steps: [ { run: x } ]\n"));
        Assert.Contains("can't have 'on_stop'", Err("jobs:\n  d:\n    environment: { name: e, action: stop, on_stop: s }\n    steps: [ { run: x } ]\n"));
    }

    // ---------------------------------------------------------------- the job graph

    private static CiJobState S(string name, PipelineStatus status, CiJobTrigger trigger = CiJobTrigger.OnSuccess, bool allowFailure = false, params string[] needs) =>
        new(name, needs, status, AllowFailure: allowFailure, Trigger: trigger);

    private static CiJobAction? Act(List<CiJobState> jobs, string name) => CiJobGraph.Next(jobs, canceled: false).Where(a => a.Job == name).Select(a => (CiJobAction?)a.Action).FirstOrDefault();

    [Fact]
    public void An_always_job_runs_after_a_failure_and_an_on_failure_job_only_then()
    {
        var failed = new List<CiJobState>
        {
            S("a", PipelineStatus.Failed), S("b", PipelineStatus.Skipped, needs: "a"),
            S("clean", PipelineStatus.Queued, CiJobTrigger.Always, needs: "b"), S("alert", PipelineStatus.Queued, CiJobTrigger.OnFailure, needs: "b"),
            S("normal", PipelineStatus.Queued, needs: "b"),
        };
        Assert.Equal(CiJobAction.Start, Act(failed, "clean"));
        Assert.Equal(CiJobAction.Start, Act(failed, "alert"));     // a failed upstream of its need
        Assert.Equal(CiJobAction.Skip, Act(failed, "normal"));

        var fine = new List<CiJobState>
        {
            S("a", PipelineStatus.Passed), S("clean", PipelineStatus.Queued, CiJobTrigger.Always, needs: "a"), S("alert", PipelineStatus.Queued, CiJobTrigger.OnFailure, needs: "a"),
        };
        Assert.Equal(CiJobAction.Start, Act(fine, "clean"));
        Assert.Equal(CiJobAction.Skip, Act(fine, "alert"));

        var allowed = new List<CiJobState> { S("a", PipelineStatus.Failed, allowFailure: true), S("alert", PipelineStatus.Queued, CiJobTrigger.OnFailure, needs: "a") };
        Assert.Equal(CiJobAction.Skip, Act(allowed, "alert"));     // an allowed failure isn't a failure

        var running = new List<CiJobState> { S("a", PipelineStatus.Running), S("clean", PipelineStatus.Queued, CiJobTrigger.Always, needs: "a") };
        Assert.Null(Act(running, "clean"));                         // still waits for its needs to finish
    }

    [Fact]
    public void The_pipeline_still_fails_when_only_an_always_job_after_the_failure_passes()
    {
        var jobs = new List<CiJobState> { S("a", PipelineStatus.Failed), S("clean", PipelineStatus.Passed, CiJobTrigger.Always, needs: "a") };
        Assert.Equal(PipelineStatus.Failed, CiJobGraph.Result(jobs, canceled: false));
    }

    [Fact]
    public void A_canceled_pipeline_cancels_always_jobs_too()
    {
        var jobs = new List<CiJobState> { S("a", PipelineStatus.Canceled), S("clean", PipelineStatus.Queued, CiJobTrigger.Always, needs: "a") };
        Assert.Equal(CiJobAction.Cancel, CiJobGraph.Next(jobs, canceled: true).Single().Action);
    }

    // ---------------------------------------------------------------- resource groups

    [Fact]
    public async Task A_resource_group_lets_one_job_hold_it_and_queues_the_others_in_order()
    {
        var groups = new CiResourceGroups();
        var first = await groups.AcquireAsync(1, "prod", CancellationToken.None);
        Assert.True(groups.IsHeld(1, "prod"));
        var other = await groups.AcquireAsync(1, "staging", CancellationToken.None);   // another group isn't blocked
        var otherRepo = await groups.AcquireAsync(2, "prod", CancellationToken.None);  // nor is another repository
        other.Dispose();
        otherRepo.Dispose();

        var second = groups.AcquireAsync(1, "prod", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);                                              // waits while the first runs
        first.Dispose();
        var handle = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(groups.IsHeld(1, "prod"));
        handle.Dispose();
        Assert.False(groups.IsHeld(1, "prod"));

        // A waiter that is canceled gives up its place.
        var held = await groups.AcquireAsync(1, "x", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = groups.AcquireAsync(1, "x", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        Assert.False(groups.IsHeld(1, "x"));
    }

    // ---------------------------------------------------------------- badges

    [Fact]
    public void Badges_are_escaped_svg_with_status_colours()
    {
        var svg = CiBadgeService.Svg(new CiBadge("pipe<line>", "a&b\"", "#4c1"));
        Assert.Contains("pipe&lt;line&gt;", svg);
        Assert.Contains("a&amp;b&quot;", svg);
        Assert.DoesNotContain("pipe<line>", svg);
        Assert.StartsWith("<svg", svg);
        Assert.Equal("passed", CiBadgeService.PipelineBadge(PipelineStatus.Passed).Value);
        Assert.Equal("failed", CiBadgeService.PipelineBadge(PipelineStatus.Failed).Value);
        Assert.Equal("unknown", CiBadgeService.PipelineBadge(null).Value);
        Assert.Equal("83.25%", CiBadgeService.CoverageBadge(83.25).Value);
        Assert.Equal("#4c1", CiBadgeService.CoverageBadge(95).Color);
        Assert.Equal("#e05d44", CiBadgeService.CoverageBadge(10).Color);
        Assert.Equal("unknown", CiBadgeService.CoverageBadge(null).Value);
    }
}

/// <summary>Badges over HTTP, restart recovery and extended pipelines in the database (no Docker; the background runner is not involved).</summary>
public class CiPipelineFeatureDbTests(TestApp app) : IClassFixture<TestApp>
{
    private async Task<(Repository Repo, Pipeline Pipeline)> SeedPipelineAsync(string status = "Passed", string branch = "main", double? coverage = null)
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        await using var db = app.Db();
        var p = new Pipeline
        {
            RepositoryId = repo.Id, Ref = branch, CommitSha = new string('a', 40), Trigger = PipelineTrigger.Manual,
            Status = Enum.Parse<PipelineStatus>(status), Coverage = coverage, FinishedAt = DateTime.UtcNow,
        };
        db.Pipelines.Add(p);
        await db.SaveChangesAsync();
        return (repo, p);
    }

    [Fact]
    public async Task Badges_need_a_token_or_a_signed_in_viewer_and_a_wrong_token_looks_like_a_missing_repository()
    {
        var (repo, _) = await SeedPipelineAsync("Failed", coverage: 91.5);
        string token;
        await using (var s = app.Scope(app.UserId("carol")))
            token = await s.Get<CiBadgeService>().EnsureTokenAsync(repo);
        Assert.Equal(48, token.Length);

        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var baseUrl = $"/p/{app.ProjectKey(repo.ProjectId)}/repos/{repo.Slug}/badges";

        var ok = await client.GetAsync($"{baseUrl}/pipeline.svg?token={token}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/svg+xml", ok.Content.Headers.ContentType!.MediaType);
        Assert.Contains("failed", await ok.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-cache", ok.Headers.CacheControl!.ToString());
        Assert.Contains("91.5%", await client.GetStringAsync($"{baseUrl}/coverage.svg?token={token}&branch=main"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{baseUrl}/pipeline.svg")).StatusCode);                       // no token, not signed in
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{baseUrl}/pipeline.svg?token=" + new string('0', 48))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{baseUrl}/coverage.svg?token={token.ToUpperInvariant()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/p/{app.ProjectKey(repo.ProjectId)}/repos/nope/badges/pipeline.svg?token={token}")).StatusCode);
        // The token belongs to one repository.
        var (other, _) = await SeedPipelineAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/p/{app.ProjectKey(other.ProjectId)}/repos/{other.Slug}/badges/pipeline.svg?token={token}")).StatusCode);
        // A branch with no pipeline is "unknown", not an error.
        Assert.Contains("unknown", await client.GetStringAsync($"{baseUrl}/pipeline.svg?token={token}&branch=nothing-here"));
    }

    [Fact]
    public async Task Only_maintainers_see_or_rotate_the_badge_token_and_rotating_breaks_the_old_one()
    {
        var project = await app.CreateProjectAsync("carol", ProjectVisibility.Private, ("bob", ProjectRole.Developer));
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        await using (var s = app.Scope(app.UserId("bob")))
        {
            Assert.Null(await s.Get<CiBadgeService>().GetTokenAsync(repo));
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<CiBadgeService>().EnsureTokenAsync(repo));
        }
        string first, second;
        await using (var s = app.Scope(app.UserId("carol")))
        {
            Assert.Null(await s.Get<CiBadgeService>().GetTokenAsync(repo));
            first = await s.Get<CiBadgeService>().EnsureTokenAsync(repo);
            Assert.Equal(first, await s.Get<CiBadgeService>().EnsureTokenAsync(repo));          // stable until rotated
            second = await s.Get<CiBadgeService>().EnsureTokenAsync(repo, rotate: true);
        }
        Assert.NotEqual(first, second);
        await using var check = app.Scope();
        Assert.False(await check.Get<CiBadgeService>().TokenValidAsync(repo.Id, first));
        Assert.True(await check.Get<CiBadgeService>().TokenValidAsync(repo.Id, second));
        await using var db = app.Db();
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "ci_badge.token_rotated" && a.ProjectId == repo.ProjectId));
    }

    [Fact]
    public async Task A_pipeline_interrupted_by_a_restart_is_queued_once_then_ends_in_error_with_retryable_jobs()
    {
        var project = await app.CreateProjectAsync("carol");
        var repo = await app.CreateRepoAsync(project.Id, "carol");
        int pipelineId, doneJob, runningJob;
        await using (var db = app.Db())
        {
            var p = new Pipeline { RepositoryId = repo.Id, Ref = "main", CommitSha = new string('b', 40), Trigger = PipelineTrigger.Push, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
            p.Jobs.Add(new PipelineJob { Name = "build", Image = "alpine", Order = 0, Status = PipelineStatus.Passed, Steps = { new PipelineStep { Name = "b", Run = "x", Order = 0, Status = PipelineStatus.Passed } } });
            p.Jobs.Add(new PipelineJob
            {
                Name = "test", Image = "alpine", Order = 1, Needs = "build", Status = PipelineStatus.Running,
                Steps = { new PipelineStep { Name = "t1", Run = "x", Order = 0, Status = PipelineStatus.Passed }, new PipelineStep { Name = "t2", Run = "y", Order = 1, Status = PipelineStatus.Running } },
            });
            p.Jobs.Add(new PipelineJob { Name = "ship", Image = "alpine", Order = 2, Needs = "test", Status = PipelineStatus.Queued, Steps = { new PipelineStep { Name = "s", Run = "z", Order = 0, Status = PipelineStatus.Queued } } });
            db.Pipelines.Add(p);
            await db.SaveChangesAsync();
            (pipelineId, doneJob, runningJob) = (p.Id, p.Jobs[0].Id, p.Jobs[1].Id);
        }

        // First restart: queued again, the finished job keeps its result, the running job starts over with a note.
        await using (var db = app.Db())
        {
            var r = await CiRecovery.RecoverInterruptedAsync(db, CancellationToken.None);
            Assert.Equal(1, r.Requeued);
        }
        await using (var db = app.Db())
        {
            var p = await db.Pipelines.AsNoTracking().Include(x => x.Jobs).ThenInclude(j => j.Steps).ThenInclude(s => s.Log).AsSplitQuery().FirstAsync(x => x.Id == pipelineId);
            Assert.Equal(PipelineStatus.Queued, p.Status);
            Assert.Equal(1, p.RecoveryAttempts);
            Assert.Null(p.Error);
            Assert.Equal(PipelineStatus.Passed, p.Jobs.Single(j => j.Id == doneJob).Status);
            var test = p.Jobs.Single(j => j.Id == runningJob);
            Assert.Equal(PipelineStatus.Queued, test.Status);
            Assert.All(test.Steps, s => Assert.Equal(PipelineStatus.Queued, s.Status));
            Assert.StartsWith("[bytebench] The server restarted while this job was running; it was queued again", test.Steps.OrderBy(s => s.Order).First().Log!.Text);
            // Pretend it started running again.
            await db.Pipelines.Where(x => x.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Running));
            await db.PipelineJobs.Where(j => j.Id == runningJob).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, PipelineStatus.Running));
            await db.PipelineSteps.Where(s => s.JobId == runningJob && s.Order == 0).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PipelineStatus.Running));
        }

        // Second restart: no loop. Error, the running job Failed (retryable), the queued one Canceled.
        await using (var db = app.Db())
        {
            var r = await CiRecovery.RecoverInterruptedAsync(db, CancellationToken.None);
            Assert.Equal(1, r.Errored);
        }
        await using (var db = app.Db())
        {
            var p = await db.Pipelines.AsNoTracking().Include(x => x.Jobs).ThenInclude(j => j.Steps).ThenInclude(s => s.Log).AsSplitQuery().FirstAsync(x => x.Id == pipelineId);
            Assert.Equal(PipelineStatus.Error, p.Status);
            Assert.Contains("restart again", p.Error);
            Assert.Equal(PipelineStatus.Failed, p.Jobs.Single(j => j.Id == runningJob).Status);
            Assert.Equal(PipelineStatus.Canceled, p.Jobs.Single(j => j.Name == "ship").Status);
            Assert.Contains("Retry the job", p.Jobs.Single(j => j.Id == runningJob).Steps.OrderBy(s => s.Order).First().Log!.Text);
            Assert.NotNull(p.FinishedAt);
        }
    }

    [Fact]
    public async Task Recovery_leaves_pipelines_that_are_not_running_alone()
    {
        var (_, passed) = await SeedPipelineAsync("Passed");
        await using var db = app.Db();
        var r = await CiRecovery.RecoverInterruptedAsync(db, CancellationToken.None);
        Assert.Equal(0, r.Requeued);
        Assert.Equal(PipelineStatus.Passed, (await db.Pipelines.AsNoTracking().FirstAsync(p => p.Id == passed.Id)).Status);
    }
}
