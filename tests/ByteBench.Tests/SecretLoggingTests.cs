using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using ByteBench.Data;
using ByteBench.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ByteBench.Tests;

/// <summary>The app with every log message (Information and up) captured in memory.</summary>
public sealed class LogCaptureApp : TestApp
{
    public ConcurrentQueue<string> Messages { get; } = new();

    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["Logging:LogLevel:ByteBench"] = "Information";
        settings["Logging:LogLevel:ByteBench.GitHttp"] = "Information";
        settings["Logging:LogLevel:ByteBench.Lfs"] = "Information";
    }

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerProvider>(new CaptureProvider(Messages));

    private sealed class CaptureProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(sink);
        public void Dispose() { }
    }

    private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(formatter(state, exception));
    }
}

/// <summary>Credentials must never reach the logs, also when a client puts them in the wrong field.</summary>
public class SecretLoggingTests(LogCaptureApp app) : IClassFixture<LogCaptureApp>
{
    [Theory]
    [InlineData("/git/WEB/web-app.git/info/refs?service=git-upload-pack")]
    [InlineData("/git/WEB/web-app.git/info/lfs/objects/batch")]
    public async Task An_access_token_sent_as_the_git_user_name_is_not_logged(string path)
    {
        // https://<token>@host/... is how other forges take tokens; here the token belongs in the password.
        var token = await app.CreateTokenAsync("bob", TokenScopes.ReadRepository);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(path.EndsWith("batch") ? HttpMethod.Post : HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(token + ":x-oauth-basic")));
        if (request.Method == HttpMethod.Post)
            request.Content = new StringContent("{\"operation\":\"download\",\"objects\":[]}", Encoding.UTF8, "application/vnd.git-lfs+json");
        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(app.Messages, m => m.Contains("Git auth failed")); // the attempt is still logged
        Assert.DoesNotContain(app.Messages, m => m.Contains(token[4..]));
    }
}
