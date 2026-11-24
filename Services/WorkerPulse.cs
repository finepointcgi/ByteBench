using ByteBench.Services.Hosting.Operations;

namespace ByteBench.Services;

/// <summary>One-line helpers for background workers to report to the <see cref="WorkerRegistry"/> (admin workers page and /metrics).</summary>
public static class WorkerPulse
{
    public static void Report(string name, bool enabled) => WorkerRegistry.Instance.Report(name, enabled);
    public static void Tick(string name) => WorkerRegistry.Instance.Tick(name);
    public static void Ok(string name) => WorkerRegistry.Instance.Success(name);
    public static void Fail(string name, Exception ex) => WorkerRegistry.Instance.Failure(name, ex);
}
