using System.Collections.Immutable;

namespace GitBench.Services;

/// <summary>Base type of everything published on <see cref="LiveEvents"/>.</summary>
public abstract record LiveEvent;

/// <summary>An issue's fields, comments or activity changed (or it was created or deleted).</summary>
public sealed record IssueChanged(int ProjectId, int IssueId) : LiveEvent;

/// <summary>Something on a project's board changed: a card moved, was created, deleted or edited.</summary>
public sealed record BoardChanged(int ProjectId) : LiveEvent;

/// <summary>A pull request's state, comments, reviews or description changed.</summary>
public sealed record PullRequestChanged(int PullRequestId, int ProjectId) : LiveEvent;

/// <summary>A user's notifications changed (new ones, or read state).</summary>
public sealed record NotificationsChanged(int UserId) : LiveEvent;

/// <summary>A CI pipeline was created, or its status, jobs, steps or logs changed.</summary>
public sealed record PipelineChanged(int PipelineId, int RepositoryId) : LiveEvent;

/// <summary>The global activity feed (dashboard) has new entries.</summary>
public sealed record ActivityChanged : LiveEvent;

/// <summary>
/// In-process pub/sub hub (singleton) that lets Interactive Server circuits refresh when another user changes data.
/// Services publish after <c>SaveChangesAsync</c>; components subscribe once interactive and dispose the
/// subscription in <c>Dispose</c>. Handlers run on the thread pool, so components must marshal with
/// <c>InvokeAsync</c>. A failing handler is logged and never affects the publisher or other subscribers.
/// </summary>
public sealed class LiveEvents(ILogger<LiveEvents> log)
{
    private readonly Lock gate = new();
    private ImmutableList<Subscription> subscriptions = [];

    /// <summary>Subscribes to every event. Dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe(Func<LiveEvent, Task> handler)
    {
        var sub = new Subscription(this, handler);
        lock (gate) subscriptions = subscriptions.Add(sub);
        return sub;
    }

    /// <summary>Subscribes to events of one type. Dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe<T>(Func<T, Task> handler) where T : LiveEvent =>
        Subscribe(e => e is T t ? handler(t) : Task.CompletedTask);

    /// <summary>Delivers the events to all current subscribers without waiting for them.</summary>
    public void Publish(params LiveEvent[] events)
    {
        var subs = subscriptions; // immutable snapshot
        foreach (var e in events)
            foreach (var s in subs)
                _ = DeliverAsync(s, e);
    }

    private async Task DeliverAsync(Subscription s, LiveEvent e)
    {
        if (s.Disposed) return;
        try { await s.Handler(e); }
        catch (ObjectDisposedException) { /* circuit went away mid-delivery */ }
        catch (Exception ex) { log.LogWarning(ex, "Live event handler failed for {Event}", e); }
    }

    private void Remove(Subscription s)
    {
        lock (gate) subscriptions = subscriptions.Remove(s);
    }

    private sealed class Subscription(LiveEvents owner, Func<LiveEvent, Task> handler) : IDisposable
    {
        public Func<LiveEvent, Task> Handler { get; } = handler;
        public volatile bool Disposed;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            owner.Remove(this);
        }
    }
}
