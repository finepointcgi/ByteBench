namespace ByteBench.Cli;

/// <summary>Entry point of the <c>bytebench</c> tool.</summary>
public static class BytebenchTool
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return await new CliApp(new CliEnvironment()).RunAsync(args, cts.Token);
    }
}
