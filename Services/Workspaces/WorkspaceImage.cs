using System.Text;
using Microsoft.Extensions.Options;

namespace ByteBench.Services.Workspaces;

public enum WorkspaceImageState { Unknown, NoDocker, Checking, Missing, Building, Ready, Failed }

/// <summary>
/// Makes sure the workspace image exists with no manual step: at startup (in the background) and on first use, if Docker is reachable and
/// the image is missing, it is built from docker/workspace/ (shipped with the app) or, when that directory isn't there, pulled.
/// Progress (the last build output lines) and failures are readable from the admin page and the start page.
/// </summary>
public sealed class WorkspaceImage(IWorkspaceBackend backend, IOptionsMonitor<WorkspaceOptions> options, IHostEnvironment env, ILogger<WorkspaceImage> log)
{
    private readonly object sync = new();
    private readonly Queue<string> tail = new();
    private Task? running;

    public WorkspaceImageState State { get; private set; } = WorkspaceImageState.Unknown;
    public string? Error { get; private set; }
    public DateTime? CheckedAt { get; private set; }

    public string Image => options.CurrentValue.Image;

    public IReadOnlyList<string> LogTail { get { lock (sync) return tail.ToList(); } }
    public string Summary { get { lock (sync) return tail.Count > 0 ? tail.Last() : ""; } }

    /// <summary>Where the Dockerfile lives: <c>Workspaces:BuildContext</c>, else docker/workspace beside the app's content or binaries.</summary>
    public string? BuildContext()
    {
        var candidates = new[] { options.CurrentValue.BuildContext, Path.Combine(env.ContentRootPath, "docker", "workspace"),
            Path.Combine(AppContext.BaseDirectory, "docker", "workspace") };
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c, "Dockerfile")));
    }

    /// <summary>Checks Docker and the image and, if the image is missing, starts building it in the background. Safe to call often.</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        lock (sync) { if (running is { IsCompleted: false }) return; }
        if (!await backend.IsAvailableAsync(ct)) { State = WorkspaceImageState.NoDocker; Error = "Docker isn't reachable from ByteBench."; CheckedAt = DateTime.UtcNow; return; }
        if (await backend.ImageExistsAsync(Image, ct)) { State = WorkspaceImageState.Ready; Error = null; CheckedAt = DateTime.UtcNow; return; }
        lock (sync)
        {
            if (running is { IsCompleted: false }) return;
            State = WorkspaceImageState.Building;
            Error = null;
            tail.Clear();
            running = Task.Run(BuildAsync, CancellationToken.None);
        }
    }

    /// <summary>Completes when a build in progress has finished (tests).</summary>
    public Task WhenIdleAsync() { lock (sync) return running ?? Task.CompletedTask; }

    private async Task BuildAsync()
    {
        try
        {
            var context = BuildContext();
            Add(context != null ? $"Building {Image} from {context} (first use; this takes a few minutes)…" : $"Pulling {Image}…");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var code = context != null ? await backend.BuildImageAsync(context, Image, Add, cts.Token) : await backend.PullImageAsync(Image, Add, cts.Token);
            if (code == 0 && await backend.ImageExistsAsync(Image, cts.Token)) { State = WorkspaceImageState.Ready; Error = null; Add("Image ready."); }
            else
            {
                State = WorkspaceImageState.Failed;
                Error = $"{(context != null ? "Building" : "Pulling")} the workspace image failed (exit {code}). Last output: {Summary}";
                log.LogWarning("Workspace image {Image} failed: {Error}", Image, Error);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            State = WorkspaceImageState.Failed;
            Error = "Building the workspace image failed: " + ex.Message;
            log.LogWarning(ex, "Workspace image build failed");
        }
        CheckedAt = DateTime.UtcNow;
    }

    private void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (sync)
        {
            tail.Enqueue(line.Length > 300 ? line[..300] : line);
            while (tail.Count > 40) tail.Dequeue();
        }
    }

    // ---------- toolchain and custom base images ----------

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> derivedGates = new(StringComparer.Ordinal);

    /// <summary>The tag of the image built for a toolchain: the configured image's tag plus the toolchain (<c>bytebench-workspace:2-node22</c>).</summary>
    public string TagFor(WorkspaceToolchain toolchain) => Derive(toolchain.Alias);

    /// <summary>The tag of the image built on a repository's own base image (<c>image:</c>, if the administrator allows them).</summary>
    public string TagFor(string baseImage) => Derive("img-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(baseImage)))[..12].ToLowerInvariant());

    private string Derive(string suffix)
    {
        var image = Image;
        var colon = image.LastIndexOf(':');
        var slash = image.LastIndexOf('/');
        return colon > slash ? $"{image}-{suffix}" : $"{image}:{suffix}";
    }

    /// <summary>
    /// Makes sure an image exists that is the workspace image's toolbox (OpenVSCode Server, git, the entrypoint) on top of <paramref name="baseImage"/>,
    /// building it from the same Dockerfile (<c>--build-arg BASE_IMAGE</c>) when it is missing. The base image comes from the server's toolchain
    /// table, or from a repository file only when an administrator allowed that. Builds of one tag never run twice at once.
    /// </summary>
    public async Task EnsureDerivedAsync(string tag, string baseImage, Action<string> onLine, CancellationToken ct)
    {
        if (!await backend.IsAvailableAsync(ct)) throw new InvalidOperationException("Docker isn't reachable from ByteBench.");
        var gate = derivedGates.GetOrAdd(tag, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await backend.ImageExistsAsync(tag, ct)) return;
            var context = BuildContext() ?? throw new InvalidOperationException("The workspace Dockerfile (docker/workspace) isn't available, so an image for this toolchain can't be built.");
            onLine($"Building the workspace image {tag} on {baseImage} (first use of this toolchain; this takes a few minutes)…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(45));
            var code = await backend.BuildImageAsync(context, tag, onLine, timeout.Token, new Dictionary<string, string> { ["BASE_IMAGE"] = baseImage });
            if (code != 0 || !await backend.ImageExistsAsync(tag, ct))
                throw new InvalidOperationException($"Building the workspace image on {baseImage} failed (exit {code}). The base image must be Debian or Ubuntu based.");
            onLine("Image ready.");
        }
        finally { gate.Release(); }
    }

    /// <summary>A sentence for the UI about the image, or null when it is ready.</summary>
    public string? Problem => State switch
    {
        WorkspaceImageState.Ready => null,
        WorkspaceImageState.NoDocker => "Docker isn't reachable from ByteBench.",
        WorkspaceImageState.Building => "The workspace image is being built (" + Summary + ").",
        WorkspaceImageState.Failed => Error,
        _ => "The workspace image isn't ready yet.",
    };
}

/// <summary>Checks Docker and builds the image shortly after startup, then rechecks occasionally (the admin installs Docker later, or deletes the image).</summary>
public sealed class WorkspaceImageStartup(WorkspaceImage image, ILogger<WorkspaceImageStartup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            WorkerPulse.Report("workspace-image", true);
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
            do
            {
                try { WorkerPulse.Tick("workspace-image"); await image.EnsureAsync(stoppingToken); WorkerPulse.Ok("workspace-image"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { WorkerPulse.Fail("workspace-image", ex); log.LogWarning(ex, "Workspace image check failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
