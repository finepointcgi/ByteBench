using System.Diagnostics;

namespace ByteBench.Tests.Infrastructure;

/// <summary><see cref="TestApp"/> on a real Kestrel port, for clients that can't use the in-memory test server (git).</summary>
public class KestrelApp : TestApp
{
    public KestrelApp()
    {
        UseKestrel(0);
        StartServer();
    }

    /// <summary>{server}/git/{key}/{slug}.git with credentials in the URL (test-only: avoids credential helpers and prompts).</summary>
    public string GitUrl(string key, string slug, string user, string password)
    {
        var b = new UriBuilder(ServerAddress) { UserName = Uri.EscapeDataString(user), Password = Uri.EscapeDataString(password) };
        b.Path = $"/git/{key}/{slug}.git";
        return b.Uri.ToString();
    }
}

/// <summary>Runs the real git CLI, isolated from the developer's global config, credential helpers and prompts.</summary>
public sealed class GitCli : IDisposable
{
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "bytebench-tests", "git-" + Guid.NewGuid().ToString("N"));

    public GitCli() => Directory.CreateDirectory(Home);

    public string Dir(string name) => Path.Combine(Home, name);

    public record Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    public async Task<Result> RunAsync(string? workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir ?? Home,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-c", "credential.helper=", "-c", "user.name=Test", "-c", "user.email=test@example.com",
                     "-c", "init.defaultBranch=main", "-c", "protocol.version=2" }.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = Home;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Home, ".gitconfig-none");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["SSH_ASKPASS"] = "";
        psi.Environment.Remove("GIT_DIR");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return new Result(p.ExitCode, await stdout + await stderr);
    }

    /// <summary>Runs git and fails the test (with git's output) if it doesn't succeed.</summary>
    public async Task<string> OkAsync(string? workDir, params string[] args)
    {
        var r = await RunAsync(workDir, args);
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed ({r.ExitCode}):\n{r.Output}");
        return r.Output;
    }

    public void Dispose()
    {
        try { TestApp.DeleteDirectory(Home); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
