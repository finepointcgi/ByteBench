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
