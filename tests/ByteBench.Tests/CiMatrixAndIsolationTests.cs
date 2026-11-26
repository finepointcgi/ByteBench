using ByteBench.Services.Ci;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ByteBench.Tests;

/// <summary><c>parallel: matrix</c> in the pipeline file, and what <c>docker run</c> gets for a step (isolation flags).</summary>
public class CiMatrixAndIsolationTests
{
    private static CiDefinition Ok(string yaml)
    {
        var r = CiFile.Parse(yaml);
        Assert.True(r.Ok, r.Error);
        return r.Definition!;
    }

    private static string Err(string yaml)
    {
        var r = CiFile.Parse(yaml);
        Assert.False(r.Ok, "expected a parse error");
        return r.Error!;
    }

    [Fact]
    public void A_matrix_expands_into_one_job_per_combination_and_dependants_need_all_of_them()
    {
        var d = Ok("""
            jobs:
              test:
                parallel:
                  matrix:
                    - SDK: ["8.0", "10.0"]
                      OS: [linux]
                    - SDK: ["9.0"]
                      OS: [alpine, "x/y"]
                variables: { COMMON: yes }
                steps: [ { run: 'echo $SDK on $OS' } ]
              report:
                needs: [test]
                steps: [ { run: echo done } ]
            """);
        var names = d.Jobs.Select(j => j.Name).ToList();
        Assert.Equal(["test-8.0-linux", "test-10.0-linux", "test-9.0-alpine", "test-9.0-x_y", "report"], names);
        var first = d.Jobs[0];
        Assert.Equal("8.0", first.Variables["SDK"]);
        Assert.Equal("linux", first.Variables["OS"]);
        Assert.Equal("yes", first.Variables["COMMON"]);
        Assert.Equal(["test-8.0-linux", "test-10.0-linux", "test-9.0-alpine", "test-9.0-x_y"], d.Jobs.Single(j => j.Name == "report").Needs);
    }

    [Fact]
    public void A_matrix_that_is_empty_too_big_or_ambiguous_is_an_error()
    {
        Assert.Contains("matrix", Err("jobs:\n  a:\n    parallel: {}\n    steps: [ { run: \"true\" } ]"));
        Assert.Contains("at least one value", Err("jobs:\n  a:\n    parallel: { matrix: [ { X: [] } ] }\n    steps: [ { run: \"true\" } ]"));
        Assert.Contains("reserved", Err("jobs:\n  a:\n    parallel: { matrix: [ { BYTEBENCH_X: [1] } ] }\n    steps: [ { run: \"true\" } ]"));
        var many = string.Join(", ", Enumerable.Range(1, 25));
        Assert.Contains("more than", Err($"jobs:\n  a:\n    parallel: {{ matrix: [ {{ X: [{many}] }} ] }}\n    steps: [ {{ run: \"true\" }} ]"));
        Assert.Contains("twice", Err("jobs:\n  a:\n    parallel: { matrix: [ { X: [1, 1] } ] }\n    steps: [ { run: \"true\" } ]"));
        Assert.Contains("can't need itself", Err("jobs:\n  a:\n    needs: [a]\n    parallel: { matrix: [ { X: [1] } ] }\n    steps: [ { run: \"true\" } ]"));
    }

    private sealed class Monitor(CiOptions o) : IOptionsMonitor<CiOptions>
    {
        public CiOptions CurrentValue => o;
        public CiOptions Get(string? name) => o;
        public IDisposable? OnChange(Action<CiOptions, string?> listener) => null;
    }

    private static List<string> Args(CiOptions o) =>
        [.. new CiDocker(new Monitor(o), NullLogger<CiDocker>.Instance).RunArguments(new CiContainerSpec(
            "bytebench-ci-1", "alpine:3.20", "echo hi", "/work/ws", new Dictionary<string, string> { ["CI"] = "true" }, "/work/env/x.env"))];

    [Fact]
    public void Step_containers_never_get_the_docker_socket_host_environment_or_extra_privileges()
    {
        var args = Args(new CiOptions());
        var line = string.Join(' ', args);
        Assert.DoesNotContain("docker.sock", line);
        Assert.DoesNotContain("--privileged", line);
        Assert.DoesNotContain("--pid", args);
        Assert.DoesNotContain("--cap-add", line);
        Assert.DoesNotContain("--user", line);                       // default: the image's user
        Assert.DoesNotContain("--cap-drop", line);
        Assert.Equal("no-new-privileges", args[args.IndexOf("--security-opt") + 1]);
        Assert.Equal("bridge", args[args.IndexOf("--network") + 1]);
        Assert.Equal("2g", args[args.IndexOf("--memory") + 1]);
        Assert.Equal("2", args[args.IndexOf("--cpus") + 1]);
        Assert.Equal("1024", args[args.IndexOf("--pids-limit") + 1]);
        Assert.Contains("--rm", args);
        Assert.Equal("/work/ws:/workspace", args[args.IndexOf("-v") + 1]);                 // only the workspace is mounted
        Assert.Single(args, a => a == "-v");
        Assert.Equal("/work/env/x.env", args[args.IndexOf("--env-file") + 1]);
        Assert.Equal(["--entrypoint", "sh", "alpine:3.20", "-e", "-c", "echo hi"], args.TakeLast(6));
    }

    [Fact]
    public void Hardening_options_add_a_user_and_drop_capabilities_and_a_network_policy_applies()
    {
        var args = Args(new CiOptions { User = "1000:1000", DropCapabilities = true, Network = "none", Memory = "", Cpus = null, PidsLimit = 0 });
        Assert.Equal("1000:1000", args[args.IndexOf("--user") + 1]);
        Assert.Equal("ALL", args[args.IndexOf("--cap-drop") + 1]);
        Assert.Equal("none", args[args.IndexOf("--network") + 1]);
        Assert.DoesNotContain("--memory", args);
        Assert.DoesNotContain("--cpus", args);
        Assert.DoesNotContain("--pids-limit", args);
    }
}
