namespace GitBench.Services.Git;

/// <summary>
/// Notified after a successful <c>git push</c> over HTTP (a <c>git-receive-pack</c> POST that
/// <c>git http-backend</c> completed with exit code 0). Register any number of implementations
/// in DI; each is called in turn, and exceptions are caught and logged so one listener cannot
/// break the push or the others.
/// </summary>
/// <remarks>
/// The call happens after the refs are updated on disk and the response has been streamed to the
/// client. Individual ref updates can still be rejected inside the protocol (e.g. non-fast-forward),
/// so listeners should read the repository state rather than assume every pushed ref landed.
/// Listeners are resolved from the request scope, so scoped services may be injected.
/// </remarks>
public interface IGitPushListener
{
    Task OnPushAsync(int repositoryId, int userId, CancellationToken ct);
}
