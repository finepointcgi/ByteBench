using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ByteBench.Cli;

/// <summary>
/// The <c>bytebench</c> command line. <see cref="RunAsync"/> never throws: it returns the exit code
/// (0 ok, 1 error, 2 usage, 3 authentication) and writes results to <c>Out</c> and problems to <c>Err</c>.
/// </summary>
public sealed partial class CliApp(CliEnvironment env)
{
    public const string Usage = """
        bytebench: ByteBench from the command line

        Usage: bytebench <command> [options]

        Commands:
          login --server <url>         Save a personal access token (read it from stdin or BYTEBENCH_TOKEN)
          logout                       Forget the saved token
          whoami                       Show who the token belongs to
          projects                     List your projects
          issues list [--project KEY] [--query GQL] [--status open|closed|all] [--limit N]
          issues show <KEY-N>
          issues create --project KEY --title TEXT [--description TEXT | --description-file FILE|-] [--type T] [--priority P] [--assignee USER] [--label NAME]...
          issues comment <KEY-N> (--body TEXT | --body-file FILE|-)
          issues move <KEY-N> --to <status>
          issues assign <KEY-N> <username> | --none
          prs list [--project KEY] [--state open|merged|declined|all]
          prs show <KEY/slug> <number>
          repos list [--project KEY]
          repos create --project KEY --name TEXT [--slug S] [--description TEXT]
          repos update <KEY/slug> [--name TEXT] [--description TEXT] [--branch DEFAULT] [--slug NEW]
          repos archive|unarchive <KEY/slug>
          repos delete <KEY/slug> --confirm "<repository name>"
          projects create --key KEY --name TEXT [--description TEXT] [--visibility private|internal] [--group ID]
          projects update <KEY> [--name TEXT] [--description TEXT] [--visibility private|internal]
          projects delete <KEY> --confirm KEY
          members list <KEY> | add <KEY> <user> --role R | role <KEY> <user> --role R | remove <KEY> <user>
          labels list <KEY> | create <KEY> --name N [--color #rrggbb] | delete <KEY> <id>
          versions list <KEY> | create <KEY> --name N [--start D] [--end D] | release|unrelease <KEY> <id>
          sprints list <KEY> | create <KEY> [--name N] [--goal G] | start <KEY> <id> --start D --end D | complete <KEY> <id> [--to backlog|new|<id>] | delete <KEY> <id>
          protection list <KEY/slug> | set <KEY/slug> --pattern P [--approvals N] [--type block-pushes|require-checks|require-signed]... | delete <KEY/slug> <id>
          webhooks list <KEY> | create <KEY> --url U --type EVENT... | delete <KEY> <id>
          pipelines list <KEY/slug> [--branch B] [--status S]
          pipelines logs <KEY/slug> <pipeline-id> [--step ID] [--tail CHARS]
          workspace connect <id> [--key FILE.pub]   Allow your SSH public key in a workspace; print the SSH config for VS Code / JetBrains
          workspace tunnel <id>        SSH ProxyCommand: relays stdin/stdout to the workspace's SSH server (used from ~/.ssh/config)
          open <KEY-N>                 Print the issue's web address
          completion bash|zsh|fish     Print a shell completion script

        Options:
          --json        Machine-readable output
          --insecure    Allow plain HTTP to a server that isn't on localhost (login)
          -h, --help    Show this help
          --version     Show the version

        Environment: BYTEBENCH_TOKEN, BYTEBENCH_SERVER, NO_COLOR.
        Exit codes: 0 ok, 1 error, 2 usage, 3 authentication.
        """;

    private ParsedArgs args = null!;

    public async Task<int> RunAsync(string[] argv, CancellationToken ct = default)
    {
        try
        {
            args = ParsedArgs.Parse(argv);
            return await DispatchAsync(ct);
        }
        catch (CliException ex)
        {
            env.Err.WriteLine("bytebench: " + Text.Line(ex.Message));
            if (ex.ExitCode == ExitCodes.Usage) env.Err.WriteLine("Run 'bytebench --help' for usage.");
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            env.Err.WriteLine("bytebench: cancelled.");
            return ExitCodes.Error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            env.Err.WriteLine("bytebench: " + Text.Line(ex.Message));
            return ExitCodes.Error;
        }
    }

    private async Task<int> DispatchAsync(CancellationToken ct)
    {
        if (args.Flag("version")) { env.Out.WriteLine("bytebench " + VersionText); return 0; }
        if (args.Words.Count == 0)
        {
            (args.Flag("help") ? env.Out : env.Err).WriteLine(Usage);
            return args.Flag("help") ? ExitCodes.Ok : ExitCodes.Usage;
        }
        if (args.Flag("help")) { env.Out.WriteLine(Usage); return ExitCodes.Ok; }

        var command = args.Words[0].ToLowerInvariant();
        var sub = args.Words.Count > 1 ? args.Words[1].ToLowerInvariant() : null;
        switch (command, sub)
        {
            case ("login", _): return await LoginAsync(ct);
            case ("logout", _): return Logout();
            case ("whoami", _): return await WhoAmIAsync(ct);
            case ("projects", "create"): return await ProjectsCreateAsync(ct);
            case ("projects", "update"): return await ProjectsUpdateAsync(ct);
            case ("projects", "delete"): return await ProjectsDeleteAsync(ct);
            case ("projects", _): return await ProjectsAsync(ct);
            case ("members", not null): return await MembersAsync(ct);
            case ("labels", not null): return await LabelsAsync(ct);
            case ("versions", not null): return await VersionsAsync(ct);
            case ("sprints", not null): return await SprintsAsync(ct);
            case ("protection", not null): return await ProtectionAsync(ct);
            case ("webhooks", not null): return await WebhooksAsync(ct);
            case ("repos", "create"): return await ReposCreateAsync(ct);
            case ("repos", "update"): return await ReposUpdateAsync(ct);
            case ("repos", "archive"): return await ReposArchiveAsync(true, ct);
            case ("repos", "unarchive"): return await ReposArchiveAsync(false, ct);
            case ("repos", "delete"): return await ReposDeleteAsync(ct);
            case ("open", _): return Open();
            case ("completion", _): return Completion();
            case ("issues", "list"): return await IssuesListAsync(ct);
            case ("issues", "show"): return await IssuesShowAsync(ct);
            case ("issues", "create"): return await IssuesCreateAsync(ct);
            case ("issues", "comment"): return await IssuesCommentAsync(ct);
            case ("issues", "move"): return await IssuesMoveAsync(ct);
            case ("issues", "assign"): return await IssuesAssignAsync(ct);
            case ("prs", "list"): return await PrsListAsync(ct);
            case ("prs", "show"): return await PrsShowAsync(ct);
            case ("repos", "list"): return await ReposListAsync(ct);
            case ("pipelines", "list"): return await PipelinesListAsync(ct);
            case ("pipelines", "logs"): return await PipelinesLogsAsync(ct);
            case ("workspace", "connect"): return await WorkspaceConnectAsync(ct);
            case ("workspace", "tunnel"): return await WorkspaceTunnelAsync(ct);
            case ("issues" or "prs" or "repos" or "pipelines" or "workspace" or "members" or "labels" or "versions" or "sprints" or "protection" or "webhooks", _):
                throw CliException.Usage($"'{command}' needs a subcommand: " + command switch
                {
                    "issues" => "list, show, create, comment, move or assign.",
                    "prs" => "list or show.",
                    "repos" => "list, create, update, archive, unarchive or delete.",
                    "members" => "list, add, role or remove.",
                    "labels" => "list, create or delete.",
                    "versions" => "list, create, release or unrelease.",
                    "sprints" => "list, create, start, complete or delete.",
                    "protection" => "list, set or delete.",
                    "webhooks" => "list, create or delete.",
                    "workspace" => "connect or tunnel.",
                    _ => "list or logs.",
                });
            default: throw CliException.Usage($"Unknown command '{Text.Line(command)}'.");
        }
    }

    // ---------------------------------------------------------------- connection

    private sealed record Connection(ApiClient Api, CliConfig Config);

    private (Uri Server, string Token, bool Insecure) Resolve()
    {
        var envServer = env.GetVariable("BYTEBENCH_SERVER");
        var envToken = env.GetVariable("BYTEBENCH_TOKEN");
        CliConfig? config = null;
        if (string.IsNullOrWhiteSpace(envServer) || string.IsNullOrWhiteSpace(envToken)) config = new ConfigStore(env.ConfigPath).Load();
        var server = !string.IsNullOrWhiteSpace(envServer) ? envServer : config?.Server;
        var token = !string.IsNullOrWhiteSpace(envToken) ? envToken.Trim() : config?.Token;
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
            throw CliException.Auth("Not logged in. Run: bytebench login --server <url>   (token on stdin or in BYTEBENCH_TOKEN)");
        var insecure = args.Flag("insecure") || config?.Insecure == true || env.GetVariable("BYTEBENCH_INSECURE") == "1";
        return (ServerUrl.Parse(server, insecure), token, insecure);
    }

    private ApiClient Connect()
    {
        var (server, token, _) = Resolve();
        return new ApiClient(server, token, env.CreateHandler());
    }

    private void Print(JsonNode? json) => env.Out.WriteLine(Json.Pretty(json));
    private bool AsJson => args.Flag("json");
    private Style Style => new(env.UseColor);

    private static string Seg(string s) => Uri.EscapeDataString(s);

    // ---------------------------------------------------------------- login, logout, whoami

    private async Task<int> LoginAsync(CancellationToken ct)
    {
        args.Allow("server");
        var serverText = args.Opt("server") ?? env.GetVariable("BYTEBENCH_SERVER");
        if (string.IsNullOrWhiteSpace(serverText)) throw CliException.Usage("login needs --server <url>.");
        var insecure = args.Flag("insecure");
        var server = ServerUrl.Parse(serverText, insecure);

        var token = env.GetVariable("BYTEBENCH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            if (!env.InputRedirected)
                throw CliException.Usage("Pipe the access token on stdin or set BYTEBENCH_TOKEN, for example:  echo \"$TOKEN\" | bytebench login --server " + ServerUrl.Display(server));
            token = (await env.In.ReadLineAsync(ct)) ?? "";
        }
        token = token.Trim();
        if (token.Length is 0 or > 200 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || !token.StartsWith("gbp_", StringComparison.Ordinal))
            throw CliException.Usage("That doesn't look like a ByteBench access token (they start with gbp_). Create one under Profile, Access tokens.");

        using var api = new ApiClient(server, token, env.CreateHandler());
        var me = (await api.GetAsync("api/v1/user", ct)).Body; // 401 here becomes exit code 3
        new ConfigStore(env.ConfigPath).Save(new CliConfig { Server = ServerUrl.Display(server), Token = token, Insecure = insecure && server.Scheme == "http" && !ServerUrl.IsLocal(server) });
        if (insecure && server.Scheme == "http" && !ServerUrl.IsLocal(server))
            env.Err.WriteLine("bytebench: warning: the token will be sent over plain HTTP to " + server.Host + ".");
        if (AsJson) Print(new JsonObject { ["server"] = ServerUrl.Display(server), ["username"] = me.S("username") });
        else env.Out.WriteLine($"Logged in to {ServerUrl.Display(server)} as {Text.Line(me.S("username"))}.");
        return ExitCodes.Ok;
    }

    private int Logout()
    {
        args.Allow();
        var removed = new ConfigStore(env.ConfigPath).Delete();
        if (AsJson) Print(new JsonObject { ["loggedOut"] = removed });
        else env.Out.WriteLine(removed ? "Logged out: the saved token was removed." : "Nothing to do: no saved token.");
        if (!string.IsNullOrEmpty(env.GetVariable("BYTEBENCH_TOKEN"))) env.Err.WriteLine("bytebench: note: BYTEBENCH_TOKEN is still set in your environment.");
        return ExitCodes.Ok;
    }

    private async Task<int> WhoAmIAsync(CancellationToken ct)
    {
        args.Allow();
        using var api = Connect();
        var me = (await api.GetAsync("api/v1/user", ct)).Body;
        if (AsJson) { Print(me); return ExitCodes.Ok; }
        var scopes = string.Join(", ", me?["scopes"].Items().Select(s => s?.GetValue<string>() ?? "") ?? []);
        env.Out.WriteLine($"{Text.Line(me.S("username"))} ({Text.Line(me.S("displayName"))}) on {ServerUrl.Display(api.Server)}" + (scopes.Length > 0 ? $"; token scopes: {Text.Line(scopes)}" : ""));
        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- projects & issues

    private async Task<int> ProjectsAsync(CancellationToken ct)
    {
        args.Allow("limit");
        using var api = Connect();
        var list = (await api.GetAsync($"api/v1/projects?perPage={args.Limit(100)}", ct)).Body;
        if (AsJson) { Print(list); return ExitCodes.Ok; }
        Text.PrintTable(env.Out, ["KEY", "NAME", "ROLE"], list.Items().Select(p => new[] { Text.Line(p.S("key")), Text.Cut(p.S("name"), 50), Text.Line(p.S("role")) }).ToList());
        return ExitCodes.Ok;
    }

    private static string IssueStatus(JsonNode? issue) => issue?["workflowStatus"] is { } w && w.S("name") is { Length: > 0 } n ? n : issue.S("status");

    private void PrintIssues(JsonNode? list)
    {
        var rows = list.Items().Select(i => new[]
        {
            Text.Line(i.S("key")), Style.Paint(Text.Line(IssueStatus(i)), i.S("status")), Text.Line(i.S("priority")),
            Text.Line(i.S("assignee", "username") is { Length: > 0 } a ? a : "-"), Text.Cut(i.S("title"), 70),
        }).ToList();
        Text.PrintTable(env.Out, ["KEY", "STATUS", "PRIORITY", "ASSIGNEE", "TITLE"], rows);
    }

    private async Task<int> IssuesListAsync(CancellationToken ct)
    {
        args.Allow("project", "query", "status", "limit");
        var limit = args.Limit(30);
        using var api = Connect();
        JsonNode? list;
        var project = args.Opt("project") is { } p ? ProjectKey(p) : null;
        if (args.Opt("query") is { } query)
        {
            if (string.IsNullOrWhiteSpace(query)) throw CliException.Usage("--query is empty.");
            var gql = project != null ? $"project = {project} AND ({query})" : query;
            list = (await api.GetAsync($"api/v1/search/issues?perPage={limit}&gql={Seg(gql)}", ct)).Body;
        }
        else if (project != null)
        {
            var status = (args.Opt("status") ?? "open").ToLowerInvariant();
            if (status is not ("open" or "closed" or "all")) throw CliException.Usage("--status must be open, closed or all.");
            list = (await api.GetAsync($"api/v1/projects/{project}/issues?status={status}&perPage={limit}", ct)).Body;
        }
        else list = (await api.GetAsync($"api/v1/search/issues?perPage={limit}&gql={Seg("assignee = currentUser()")}", ct)).Body;
        if (AsJson) { Print(list); return ExitCodes.Ok; }
        PrintIssues(list);
        return ExitCodes.Ok;
    }

    [GeneratedRegex("^([A-Za-z][A-Za-z0-9]{0,15})-([1-9][0-9]{0,8})$")]
    private static partial Regex IssueKeyRx();

    private static (string Project, int Number) IssueKey(string value)
    {
        var m = IssueKeyRx().Match(value.Trim());
        if (!m.Success) throw CliException.Usage($"'{Text.Line(value)}' is not an issue key. Use PROJECT-123.");
        return (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value));
    }

    private static string ProjectKey(string value) =>
        Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9]{0,15}$") ? value.ToUpperInvariant() : throw CliException.Usage($"'{Text.Line(value)}' is not a project key.");

    private (string Key, string Slug) RepoName(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[A-Za-z][A-Za-z0-9]{0,15}$") || !Regex.IsMatch(parts[1], "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$"))
            throw CliException.Usage($"'{Text.Line(value)}' is not a repository. Use PROJECT/slug (see: bytebench repos list).");
        return (parts[0].ToUpperInvariant(), parts[1].ToLowerInvariant());
    }

    private string OneWord(int index, string what)
    {
        if (args.Words.Count <= index) throw CliException.Usage($"Missing {what}.");
        return args.Words[index];
    }

    private void NoExtraWords(int expected)
    {
        if (args.Words.Count > expected) throw CliException.Usage($"Unexpected argument '{Text.Line(args.Words[expected])}'.");
    }

    private async Task<int> IssuesShowAsync(CancellationToken ct)
    {
        args.Allow();
        var (project, number) = IssueKey(OneWord(2, "the issue key"));
        NoExtraWords(3);
        using var api = Connect();
        var issue = (await api.GetAsync($"api/v1/projects/{project}/issues/{number}", ct)).Body;
        var comments = (await api.GetAsync($"api/v1/projects/{project}/issues/{number}/comments?perPage=100", ct)).Body;
        if (AsJson) { Print(new JsonObject { ["issue"] = issue?.DeepClone(), ["comments"] = comments?.DeepClone() }); return ExitCodes.Ok; }
        var w = env.Out;
        w.WriteLine($"{Text.Line(issue.S("key"))}  {Text.Line(issue.S("title"))}");
        w.WriteLine($"Status: {Style.Paint(Text.Line(IssueStatus(issue)), issue.S("status"))}   Type: {Text.Line(issue.S("type"))}   Priority: {Text.Line(issue.S("priority"))}   " +
                    $"Assignee: {(issue.S("assignee", "username") is { Length: > 0 } a ? Text.Line(a) : "-")}   Reporter: {Text.Line(issue.S("reporter", "username"))}");
        var labels = string.Join(", ", issue?["labels"].Items().Select(l => l.S("name")) ?? []);
        if (labels.Length > 0) w.WriteLine("Labels: " + Text.Line(labels));
        w.WriteLine("URL: " + Text.Line(issue.S("webUrl")));
        if (issue.S("description") is { Length: > 0 } d) { w.WriteLine(); w.WriteLine(Text.Safe(d)); }
        foreach (var c in comments.Items())
        {
            w.WriteLine();
            w.WriteLine($"-- {Text.Line(c.S("author", "username"))}, {Text.Line(c.S("createdAt"))}");
            w.WriteLine(Text.Safe(c.S("body")));
        }
        return ExitCodes.Ok;
    }

    private async Task<string?> ReadTextAsync(string? inline, string? file, string name, CancellationToken ct)
    {
        if (inline != null && file != null) throw CliException.Usage($"Use either --{name} or --{name}-file, not both.");
        if (inline != null) return inline;
        if (file == null) return null;
        if (file == "-")
        {
            if (!env.InputRedirected) throw CliException.Usage($"--{name}-file - reads standard input, but it is a terminal.");
            return await env.In.ReadToEndAsync(ct);
        }
        var info = new FileInfo(file);
        if (!info.Exists) throw CliException.Usage($"File '{Text.Line(file)}' does not exist.");
        if (info.Length > 1_000_000) throw CliException.Usage($"File '{Text.Line(file)}' is too large (limit 1 MB).");
        return await File.ReadAllTextAsync(file, ct);
    }

    private async Task<int> IssuesCreateAsync(CancellationToken ct)
    {
        args.Allow("project", "title", "description", "description-file", "type", "priority", "assignee", "label");
        NoExtraWords(2);
        var project = ProjectKey(args.Required("project"));
        var body = new JsonObject { ["title"] = args.Required("title") };
        if (await ReadTextAsync(args.Opt("description"), args.Opt("description-file"), "description", ct) is { } d) body["description"] = d;
        if (args.Opt("type") is { } t) body["type"] = t.ToLowerInvariant();
        if (args.Opt("priority") is { } pr) body["priority"] = pr.ToLowerInvariant();
        if (args.Opt("assignee") is { } a) body["assignee"] = a;
        if (args.All("label").Count > 0) body["labels"] = new JsonArray(args.All("label").Select(l => (JsonNode)l).ToArray());
        using var api = Connect();
        var issue = (await api.PostAsync($"api/v1/projects/{project}/issues", body, ct)).Body;
        if (AsJson) Print(issue);
        else env.Out.WriteLine($"Created {Text.Line(issue.S("key"))}: {Text.Line(issue.S("webUrl"))}");
        return ExitCodes.Ok;
    }

    private async Task<int> IssuesCommentAsync(CancellationToken ct)
    {
        args.Allow("body", "body-file");
        var (project, number) = IssueKey(OneWord(2, "the issue key"));
        NoExtraWords(3);
        var text = await ReadTextAsync(args.Opt("body"), args.Opt("body-file"), "body", ct);
        if (string.IsNullOrWhiteSpace(text)) throw CliException.Usage("Give the comment with --body or --body-file.");
        using var api = Connect();
        var comment = (await api.PostAsync($"api/v1/projects/{project}/issues/{number}/comments", new { body = text }, ct)).Body;
        if (AsJson) Print(comment);
        else env.Out.WriteLine($"Commented on {project}-{number}.");
        return ExitCodes.Ok;
    }

    private async Task<int> IssuesMoveAsync(CancellationToken ct)
    {
        args.Allow("to");
        var (project, number) = IssueKey(OneWord(2, "the issue key"));
        NoExtraWords(3);
        var to = args.Required("to").Trim();
        using var api = Connect();
        var workflow = (await api.GetAsync($"api/v1/projects/{project}/workflow", ct)).Body;
        var statuses = workflow?["statuses"].Items().ToList() ?? [];
        object patch;
        if (statuses.FirstOrDefault(s => s.S("name").Equals(to, StringComparison.OrdinalIgnoreCase)) is { } byName) patch = new { workflowStatusId = byName.I("id") };
        else
        {
            var category = to.Replace(" ", "").Replace("-", "").Replace("_", "");
            var known = new[] { "backlog", "todo", "inProgress", "inReview", "done" }.FirstOrDefault(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (known == null)
                throw CliException.Failure($"Unknown status '{Text.Line(to)}'. This project's statuses: {string.Join(", ", statuses.Select(s => Text.Line(s.S("name"))))}.");
            patch = new { status = known };
        }
        var issue = (await api.PatchAsync($"api/v1/projects/{project}/issues/{number}", patch, ct)).Body;
        if (AsJson) Print(issue);
        else env.Out.WriteLine($"{Text.Line(issue.S("key"))} is now {Text.Line(IssueStatus(issue))}.");
        return ExitCodes.Ok;
    }

    private async Task<int> IssuesAssignAsync(CancellationToken ct)
    {
        args.Allow("none");
        var (project, number) = IssueKey(OneWord(2, "the issue key"));
        object patch;
        if (args.Flag("none"))
        {
            NoExtraWords(3);
            patch = new { assignee = (string?)null };
        }
        else
        {
            var user = OneWord(3, "the username (or use --none)");
            NoExtraWords(4);
            patch = new { assignee = user };
        }
        using var api = Connect();
        var issue = (await api.PatchAsync($"api/v1/projects/{project}/issues/{number}", patch, ct)).Body;
        if (AsJson) Print(issue);
        else env.Out.WriteLine(issue.S("assignee", "username") is { Length: > 0 } a ? $"{Text.Line(issue.S("key"))} is assigned to {Text.Line(a)}." : $"{Text.Line(issue.S("key"))} is unassigned.");
        return ExitCodes.Ok;
    }

    private int Open()
    {
        args.Allow();
        var (project, number) = IssueKey(OneWord(1, "the issue key"));
        NoExtraWords(2);
        var (server, _, _) = Resolve();
        var url = $"{ServerUrl.Display(server)}/p/{project}/issues/{number}";
        if (AsJson) Print(new JsonObject { ["url"] = url });
        else env.Out.WriteLine(url);
        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- repositories, pull requests, pipelines

    private async Task<List<string>> ProjectKeysAsync(ApiClient api, CancellationToken ct) =>
        (await api.GetAsync("api/v1/projects?perPage=100", ct)).Body.Items().Select(p => p.S("key")).Where(k => k.Length > 0).ToList();

    private async Task<int> ReposListAsync(CancellationToken ct)
    {
        args.Allow("project");
        NoExtraWords(2);
        using var api = Connect();
        var projects = args.Opt("project") is { } p ? [ProjectKey(p)] : await ProjectKeysAsync(api, ct);
        var all = new JsonArray();
        var rows = new List<string[]>();
        foreach (var key in projects)
            foreach (var r in (await api.GetAsync($"api/v1/projects/{key}/repos?perPage=100", ct)).Body.Items())
            {
                all.Add(r?.DeepClone());
                rows.Add([$"{key}/{Text.Line(r.S("slug"))}", Text.Line(r.S("defaultBranch")), Text.Line(r.S("cloneUrl"))]);
            }
        if (AsJson) Print(all);
        else Text.PrintTable(env.Out, ["REPOSITORY", "BRANCH", "CLONE URL"], rows);
        return ExitCodes.Ok;
    }

    private async Task<int> PrsListAsync(CancellationToken ct)
    {
        args.Allow("project", "state", "limit");
        NoExtraWords(2);
        var state = (args.Opt("state") ?? "open").ToLowerInvariant();
        if (state is not ("open" or "merged" or "declined" or "all")) throw CliException.Usage("--state must be open, merged, declined or all.");
        var limit = args.Limit(30);
        using var api = Connect();
        var projects = args.Opt("project") is { } p ? [ProjectKey(p)] : await ProjectKeysAsync(api, ct);
        var all = new JsonArray();
        var rows = new List<string[]>();
        foreach (var key in projects)
            foreach (var pr in (await api.GetAsync($"api/v1/projects/{key}/pull-requests?state={state}&perPage={limit}", ct)).Body.Items())
            {
                if (all.Count >= limit) break;
                all.Add(pr?.DeepClone());
                rows.Add([$"{key}/{Text.Line(pr.S("repository"))}#{pr.I("number")}", Style.Paint(Text.Line(pr.S("state")), pr.S("state")),
                    Text.Line(pr.S("author", "username")), Text.Cut(pr.S("title"), 70)]);
            }
        if (AsJson) Print(all);
        else Text.PrintTable(env.Out, ["PULL REQUEST", "STATE", "AUTHOR", "TITLE"], rows);
        return ExitCodes.Ok;
    }

    private async Task<int> PrsShowAsync(CancellationToken ct)
    {
        args.Allow();
        var word = OneWord(2, "the repository (PROJECT/slug)");
        string number;
        if (word.Contains('#')) (word, number) = (word[..word.IndexOf('#')], word[(word.IndexOf('#') + 1)..]);
        else { number = OneWord(3, "the pull request number"); NoExtraWords(4); }
        var (key, slug) = RepoName(word);
        if (!int.TryParse(number, out var n) || n < 1) throw CliException.Usage("The pull request number must be a positive integer.");
        using var api = Connect();
        var pr = (await api.GetAsync($"api/v1/projects/{key}/repos/{Seg(slug)}/pull-requests/{n}", ct)).Body;
        var comments = (await api.GetAsync($"api/v1/projects/{key}/repos/{Seg(slug)}/pull-requests/{n}/comments?perPage=100", ct)).Body;
        if (AsJson) { Print(new JsonObject { ["pullRequest"] = pr?.DeepClone(), ["comments"] = comments?.DeepClone() }); return ExitCodes.Ok; }
        var w = env.Out;
        w.WriteLine($"{key}/{slug}#{n}  {Text.Line(pr.S("title"))}");
        w.WriteLine($"State: {Style.Paint(Text.Line(pr.S("state")), pr.S("state"))}{(pr.B("isDraft") ? " (draft)" : "")}   {Text.Line(pr.S("sourceBranch"))} -> {Text.Line(pr.S("targetBranch"))}   Author: {Text.Line(pr.S("author", "username"))}");
        var reviews = string.Join(", ", pr?["reviews"].Items().Select(r => $"{r.S("user", "username")}: {r.S("state")}") ?? []);
        if (reviews.Length > 0) w.WriteLine("Reviews: " + Text.Line(reviews));
        w.WriteLine("URL: " + Text.Line(pr.S("webUrl")));
        if (pr.S("description") is { Length: > 0 } d) { w.WriteLine(); w.WriteLine(Text.Safe(d)); }
        foreach (var c in comments.Items())
        {
            w.WriteLine();
            w.WriteLine($"-- {Text.Line(c.S("author", "username"))}{(c.S("path") is { Length: > 0 } path ? " on " + Text.Line(path) : "")}");
            w.WriteLine(Text.Safe(c.S("body")));
        }
        return ExitCodes.Ok;
    }

    private async Task<int> PipelinesListAsync(CancellationToken ct)
    {
        args.Allow("branch", "status", "limit");
        var (key, slug) = RepoName(OneWord(2, "the repository (PROJECT/slug)"));
        NoExtraWords(3);
        var query = new List<string> { $"perPage={args.Limit(20)}" };
        if (args.Opt("branch") is { } b) query.Add("ref=" + Seg(b));
        if (args.Opt("status") is { } s) query.Add("status=" + Seg(s.ToLowerInvariant()));
        using var api = Connect();
        var list = (await api.GetAsync($"api/v1/projects/{key}/repos/{Seg(slug)}/pipelines?" + string.Join('&', query), ct)).Body;
        if (AsJson) { Print(list); return ExitCodes.Ok; }
        Text.PrintTable(env.Out, ["ID", "STATUS", "BRANCH", "COMMIT", "MESSAGE"], list.Items().Select(p => new[]
        {
            p.I("id").ToString(), Style.Paint(Text.Line(p.S("status")), p.S("status")), Text.Cut(p.S("ref"), 30),
            Text.Line(p.S("commitSha") is { Length: > 8 } sha ? sha[..8] : p.S("commitSha")), Text.Cut(p.S("commitMessage"), 60),
        }).ToList());
        return ExitCodes.Ok;
    }

    private async Task<int> PipelinesLogsAsync(CancellationToken ct)
    {
        args.Allow("step", "tail");
        var (key, slug) = RepoName(OneWord(2, "the repository (PROJECT/slug)"));
        if (!int.TryParse(OneWord(3, "the pipeline id"), out var id) || id < 1) throw CliException.Usage("The pipeline id must be a positive integer.");
        NoExtraWords(4);
        var tail = 20_000;
        if (args.Opt("tail") is { } t && (!int.TryParse(t, out tail) || tail is < 1 or > 1_000_000)) throw CliException.Usage("--tail must be a number of characters from 1 to 1000000.");
        using var api = Connect();
        var pipeline = (await api.GetAsync($"api/v1/projects/{key}/repos/{Seg(slug)}/pipelines/{id}", ct)).Body;
        var steps = pipeline?["jobs"].Items().SelectMany(j => j?["steps"].Items().Select(s => (Job: j.S("name"), Step: s)) ?? []).ToList() ?? [];
        if (steps.Count == 0) throw CliException.Failure($"Pipeline {id} has no steps yet.");
        (string Job, JsonNode? Step) chosen;
        if (args.Opt("step") is { } stepText)
        {
            if (!int.TryParse(stepText, out var stepId)) throw CliException.Usage("--step must be a step id (see: bytebench pipelines logs ... without --step).");
            chosen = steps.FirstOrDefault(x => x.Step.I("id") == stepId) is { Step: not null } f ? f : throw CliException.Failure($"Step {stepId} is not part of pipeline {id}.");
        }
        else chosen = steps.FirstOrDefault(x => x.Step.S("status") is "failed" or "error") is { Step: not null } failed ? failed : steps[^1];

        var log = await api.GetAsync($"api/v1/projects/{key}/repos/{Seg(slug)}/pipelines/{id}/steps/{chosen.Step.I("id")}/log?tail={tail}", ct);
        if (AsJson)
        {
            Print(new JsonObject { ["pipelineId"] = id, ["stepId"] = chosen.Step.I("id"), ["job"] = chosen.Job, ["step"] = chosen.Step.S("name"), ["status"] = chosen.Step.S("status"), ["log"] = log.Text });
            return ExitCodes.Ok;
        }
        env.Err.WriteLine($"Pipeline #{id} [{Text.Line(pipeline.S("status"))}], job {Text.Line(chosen.Job)}, step {chosen.Step.I("id")} \"{Text.Line(chosen.Step.S("name"))}\" [{Text.Line(chosen.Step.S("status"))}]");
        env.Out.Write(Text.Safe(log.Text));
        if (log.Text.Length > 0 && !log.Text.EndsWith('\n')) env.Out.WriteLine();
        return ExitCodes.Ok;
    }
}
