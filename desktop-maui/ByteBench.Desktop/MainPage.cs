using System.Net.Http.Headers;
using ByteBench.Desktop.Controls;
using ByteBench.Desktop.Core;
using ByteBench.Desktop.Services;

namespace ByteBench.Desktop;

/// <summary>
/// The whole UI: a stack of per-server web views, a server-address/management panel (first run, add, switch, tokens), an error panel
/// for unreachable servers, and the menu bar. Everything else is the server's own web UI inside the web view.
/// </summary>
#pragma warning disable CS0618 // DisplayAlert / DisplayActionSheet / DisplayPromptAsync: obsolete in newer MAUI in favour of *Async names; still work.
public sealed class MainPage : ContentPage
{
    private readonly ProfileStore _store;
    private readonly ITokenStore _tokens;
    private readonly NotificationPoller _poller;
    private readonly OsNotifier _os;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    private readonly Dictionary<string, ProfileWebView> _views = [];
    private readonly Grid _browserHost = new();
    private readonly Label _banner;
    private readonly VerticalStackLayout _errorPanel;
    private readonly Label _errorText;
    private readonly ScrollView _serversPanel;
    private readonly Entry _addressEntry;
    private readonly Entry _nameEntry;
    private readonly Label _addStatus;
    private readonly VerticalStackLayout _serverList = new() { Spacing = 8 };
    private readonly Picker _tokenServer = new() { Title = "Server" };
    private readonly Entry _tokenEntry;
    private readonly Label _tokenStatus;
    private readonly Button _closeServers;
    private string? _activeId;
    private int _totalUnread;

    public MainPage(ProfileStore store, ITokenStore tokens, NotificationPoller poller, OsNotifier os, HttpClient http)
    {
        _store = store;
        _tokens = tokens;
        _poller = poller;
        _os = os;
        _http = http;

        _banner = Text("", 12);
        _banner.IsVisible = false;
        _banner.Padding = new Thickness(12, 6);

        _errorText = Text("", 14);
        var retry = new Button { Text = "Try again" };
        retry.Clicked += (_, _) => { HideError(); ActiveView()?.Reload(); };
        var servers = new Button { Text = "Servers..." };
        servers.Clicked += (_, _) => ShowServers();
        _errorPanel = new VerticalStackLayout
        {
            Spacing = 12, Padding = 32, IsVisible = false, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center, MaximumWidthRequest = 520,
            Children = { Text("This server could not be opened", 20, bold: true), _errorText, new HorizontalStackLayout { Spacing = 8, Children = { retry, servers } } },
        };

        _addressEntry = new Entry { Placeholder = "bytebench.example.com or localhost:5108", ReturnType = ReturnType.Go };
        _addressEntry.Completed += async (_, _) => await AddServerAsync();
        _nameEntry = new Entry { Placeholder = "Name (optional)" };
        _addStatus = Text("", 13);
        var add = new Button { Text = "Add server" };
        add.Clicked += async (_, _) => await AddServerAsync();

        _tokenEntry = new Entry { Placeholder = "API token", IsPassword = true };
        _tokenStatus = Text("", 13);
        var saveToken = new Button { Text = "Save token" };
        saveToken.Clicked += async (_, _) => await SaveTokenAsync();
        var clearToken = new Button { Text = "Remove token" };
        clearToken.Clicked += async (_, _) => await ClearTokenAsync();

        _closeServers = new Button { Text = "Back to ByteBench", HorizontalOptions = LayoutOptions.Start };
        _closeServers.Clicked += (_, _) => HideServers();

        _serversPanel = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12, Padding = 32, MaximumWidthRequest = 640, HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    Text("ByteBench servers", 24, bold: true),
                    Text("Enter the address of your ByteBench server, for example https://bytebench.example.com or localhost:5108. Each server keeps its own sign-in.", 14),
                    _addressEntry, _nameEntry, add, _addStatus,
                    Text("Saved servers", 18, bold: true),
                    _serverList,
                    Text("API token (optional)", 18, bold: true),
                    Text("A token enables desktop notifications and the unread badge for a server, and lets Clone use your account. It is kept in the system's secure store, never in a file.", 13),
                    _tokenServer, _tokenEntry, new HorizontalStackLayout { Spacing = 8, Children = { saveToken, clearToken } }, _tokenStatus,
                    _closeServers,
                },
            },
        };
        _serversPanel.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#1b1b1f"));

        var browserColumn = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        browserColumn.Add(_banner, 0, 0);
        browserColumn.Add(_browserHost, 0, 1);

        var root = new Grid();
        root.Add(browserColumn);
        root.Add(_errorPanel);
        root.Add(_serversPanel);
        Content = root;

        _poller.NotificationAnnounced += (p, n, path) => _os.Show(p.Id, path, string.IsNullOrEmpty(n.Actor) ? p.Name : $"{p.Name}: {n.Actor}", n.Title);
        _poller.MoreAnnounced += (p, more) => _os.Show(p.Id, "/notifications", p.Name, $"{more} more new notifications");
        _poller.UnreadChanged += (_, _, total) => MainThread.BeginInvokeOnMainThread(() => { _totalUnread = total; _os.SetBadge(total); UpdateTitle(); RebuildMenu(); });
        _os.Clicked += (profileId, path) => MainThread.BeginInvokeOnMainThread(async () => await OpenProfileAsync(profileId, path));
        _store.Changed += () => MainThread.BeginInvokeOnMainThread(() => { RebuildServerList(); RebuildMenu(); });

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        _os.Initialize();
        RebuildServerList();
        RebuildMenu();
        DeepLinkRouter.Attach(raw => MainThread.BeginInvokeOnMainThread(async () => await HandleDeepLinkAsync(raw)));
        if (DeepLink.Extract(Environment.GetCommandLineArgs()) is { } fromArgs) await HandleDeepLinkAsync(fromArgs);

        if (_activeId is null)
        {
            var last = _store.Settings().LastProfileId;
            var first = _store.List().FirstOrDefault(p => p.Id == last) ?? _store.List().FirstOrDefault();
            if (first is null) ShowServers(); // first run: ask for a server address
            else await OpenProfileAsync(first.Id);
        }
        _ = Task.Run(() => _poller.RunAsync(_cts.Token));
    }

    // ---------------------------------------------------------------- opening servers

    private ProfileWebView? ActiveView() => _activeId is { } id && _views.TryGetValue(id, out var v) ? v : null;

    private async Task OpenProfileAsync(string id, string? path = null)
    {
        var profile = _store.Get(id);
        if (profile is null) return;
        var target = path is not null && DeepLink.IsSafePath(path, profile.Origin) ? path : "/";
        if (path is not null && target != path) target = "/notifications"; // an unsafe path from a notification: show the inbox instead
        if (!_views.TryGetValue(id, out var view))
        {
            view = new ProfileWebView(profile) { HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
            view.Navigated += (v, _) => { if (v == ActiveView()) HideError(); };
            view.NavigationFailed += (v, args) => { if (v == ActiveView()) ShowError(args.Message); };
            _views[id] = view;
            _browserHost.Add(view);
            _ = LoadServerInfoAsync(profile, view);
            view.Navigate(new Uri(new Uri(profile.Origin), target).ToString());
        }
        else if (path is not null)
        {
            view.Navigate(new Uri(new Uri(profile.Origin), target).ToString());
        }

        _activeId = id;
        foreach (var (key, v) in _views) v.IsVisible = key == id;
        _store.MarkOpened(id);
        HideError();
        HideServers();
        _banner.IsVisible = !string.IsNullOrEmpty(_banner.Text) && _bannerFor == id;
        UpdateTitle();
        RebuildMenu();
    }

    private string? _bannerFor;

    private async Task LoadServerInfoAsync(ServerProfile profile, ProfileWebView view)
    {
        try
        {
            using var res = await _http.GetAsync(new Uri(new Uri(profile.Origin), "/api/v1/server-info"), _cts.Token);
            var compat = ServerApi.EvaluateServerInfo((int)res.StatusCode, await res.Content.ReadAsStringAsync(_cts.Token));
            view.AdvertisedWorkspaceBase = compat.Info?.WorkspaceBaseDomain;
            if (compat.Info is { } info) _capabilities[profile.Id] = info.Capabilities;
            if (compat.Level is CompatLevel.Legacy or CompatLevel.Newer && compat.Message is { } m)
                MainThread.BeginInvokeOnMainThread(() => { _banner.Text = m; _bannerFor = profile.Id; _banner.IsVisible = _activeId == profile.Id; });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* the web view reports an unreachable server itself */ }
    }

    private void UpdateTitle()
    {
        var name = _activeId is { } id ? _store.Get(id)?.Name : null;
        if (Window is { } w) w.Title = (_totalUnread > 0 ? $"({_totalUnread}) " : "") + (name is null ? "ByteBench" : $"{name} - ByteBench");
    }

    private void ShowError(string message) { _errorText.Text = message; _errorPanel.IsVisible = true; }
    private void HideError() => _errorPanel.IsVisible = false;

    private void ShowServers()
    {
        RebuildServerList();
        _closeServers.IsVisible = _activeId is not null;
        _serversPanel.IsVisible = true;
        _addressEntry.Focus();
    }

    private void HideServers() => _serversPanel.IsVisible = false;

    // ---------------------------------------------------------------- add / remove / token

    private async Task AddServerAsync()
    {
        _addStatus.Text = "";
        var address = _addressEntry.Text;
        var n = ServerUrl.Normalize(address);
        var confirmed = false;
        if (!n.Ok && n.NeedsHttpConfirmation)
        {
            if (!await DisplayAlert("Plain http", n.Error, "Use http anyway", "Cancel")) return;
            confirmed = true;
            n = ServerUrl.Normalize(address, allowInsecureRemote: true);
        }
        if (!n.Ok) { _addStatus.Text = n.Error ?? "That address cannot be used."; return; }

        _addStatus.Text = "Checking the server...";
        Compat? compat = null;
        try
        {
            using var res = await _http.GetAsync(new Uri(new Uri(n.Origin), "/api/v1/server-info"), _cts.Token);
            compat = ServerApi.EvaluateServerInfo((int)res.StatusCode, await res.Content.ReadAsStringAsync(_cts.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { compat = ServerApi.EvaluateServerInfo(null, null); }
        _addStatus.Text = "";
        if (compat.Level is CompatLevel.Unavailable or CompatLevel.NotByteBench
            && !await DisplayAlert("Add this server?", compat.Message + " Add it anyway?", "Add", "Cancel")) return;

        var added = _store.Add(_nameEntry.Text, address, confirmed);
        if (!added.Ok) { _addStatus.Text = added.Error ?? "Could not add the server."; return; }
        _addressEntry.Text = "";
        _nameEntry.Text = "";
        if (compat.Level == CompatLevel.Legacy) await DisplayAlert("Older server", compat.Message, "OK");
        await OpenProfileAsync(added.Value!.Id);
    }

    private async Task RemoveServerAsync(ServerProfile p)
    {
        if (!await DisplayAlert("Remove server", $"Remove {p.Name}? Its saved sign-in and token on this computer are deleted.", "Remove", "Cancel")) return;
        if (_views.Remove(p.Id, out var view)) _browserHost.Remove(view); // handler disconnects: the web view is closed before its data is deleted
        _store.Remove(p.Id);
        _poller.Forget(p.Id);
        await _tokens.DeleteAsync(p.Id);
        await ProfileData.RemoveAsync(p.Id);
        if (_activeId == p.Id)
        {
            _activeId = null;
            var next = _store.List().FirstOrDefault();
            if (next is not null) await OpenProfileAsync(next.Id); else ShowServers();
        }
    }

    private async Task RenameServerAsync(ServerProfile p)
    {
        var name = await DisplayPromptAsync("Rename server", "Name:", initialValue: p.Name, maxLength: 60);
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.Rename(p.Id, name);
        RebuildServerList();
        RebuildMenu();
        UpdateTitle();
    }

    private ServerProfile? SelectedTokenServer() =>
        _tokenServer.SelectedIndex >= 0 && _tokenServer.SelectedIndex < _tokenServer.ItemsSource?.Count ? _store.List().ElementAtOrDefault(_tokenServer.SelectedIndex) : null;

    private async Task SaveTokenAsync()
    {
        _tokenStatus.Text = "";
        var p = SelectedTokenServer();
        var token = _tokenEntry.Text?.Trim();
        if (p is null || string.IsNullOrEmpty(token)) { _tokenStatus.Text = "Choose a server and paste a token."; return; }
        var user = await FetchUserNameAsync(p, token);
        if (user.Status is 401 or 403) { _tokenStatus.Text = "The server did not accept that token. It was not saved."; return; }
        if (!await _tokens.SetAsync(p.Id, token))
        {
            _tokenStatus.Text = "The system's secure store is not available to this build, so the token was NOT saved (it is never written to a plain file). See the README about signing the Mac build.";
            return;
        }
        _tokenEntry.Text = "";
        _poller.PollSoon();
        _tokenStatus.Text = user.Status == 200 ? $"Token saved. Notifications for {p.Name} are on." : "Token saved, but the server could not be reached to check it.";
    }

    private async Task ClearTokenAsync()
    {
        if (SelectedTokenServer() is not { } p) return;
        await _tokens.DeleteAsync(p.Id);
        _poller.Forget(p.Id);
        _tokenStatus.Text = $"Token removed. Notifications for {p.Name} are off.";
    }

    private async Task<(int? Status, string? UserName)> FetchUserNameAsync(ServerProfile p, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(p.Origin), "/api/v1/user"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _http.SendAsync(req, _cts.Token);
            return ((int)res.StatusCode, res.IsSuccessStatusCode ? ServerApi.ParseUserName(await res.Content.ReadAsStringAsync(_cts.Token)) : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, null); }
    }

    // ---------------------------------------------------------------- server list and menu

    private void RebuildServerList()
    {
        var profiles = _store.List();
        _serverList.Children.Clear();
        if (profiles.Count == 0) _serverList.Children.Add(Text("No servers yet.", 13));
        foreach (var p in profiles)
        {
            var unread = _poller.UnreadFor(p.Id);
            var open = new Button { Text = "Open" };
            open.Clicked += async (_, _) => await OpenProfileAsync(p.Id);
            var rename = new Button { Text = "Rename" };
            rename.Clicked += async (_, _) => await RenameServerAsync(p);
            var remove = new Button { Text = "Remove" };
            remove.Clicked += async (_, _) => await RemoveServerAsync(p);
            _serverList.Children.Add(new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    Text(p.Name + (unread > 0 ? $"  ({unread} unread)" : ""), 15, bold: true),
                    Text(p.Origin, 12),
                    new HorizontalStackLayout { Spacing = 8, Children = { open, rename, remove } },
                },
            });
        }
        var previous = _tokenServer.SelectedIndex;
        _tokenServer.ItemsSource = profiles.Select(p => p.Name).ToList();
        if (profiles.Count > 0) _tokenServer.SelectedIndex = Math.Clamp(previous, 0, profiles.Count - 1);
    }

    private static KeyboardAcceleratorModifiers Primary =>
        DeviceInfo.Platform == DevicePlatform.MacCatalyst ? KeyboardAcceleratorModifiers.Cmd : KeyboardAcceleratorModifiers.Ctrl;

    private static MenuFlyoutItem Item(string text, Action action, string? key = null, bool shift = false)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Clicked += (_, _) => action();
        if (key is not null)
            item.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = Primary | (shift ? KeyboardAcceleratorModifiers.Shift : KeyboardAcceleratorModifiers.None), Key = key });
        return item;
    }

    private void RebuildMenu()
    {
        var profiles = _store.List();
        var file = new MenuBarItem { Text = "File" };
        file.Add(Item("Switch Server...", () => _ = SwitchServerAsync(), "K"));
        file.Add(Item("Add or Manage Servers...", ShowServers, "N", shift: true));
        file.Add(Item("Clone This Repository...", () => _ = CloneAsync(), "C", shift: true));
        if (LinkRegistration.IsNeeded)
            file.Add(Item("Register bytebench:// Links with Windows", () => _ = RegisterLinksAsync()));

        var view = new MenuBarItem { Text = "View" };
        view.Add(Item("Reload", () => { HideError(); ActiveView()?.Reload(); }, "R"));
        view.Add(Item("Back", () => ActiveView()?.GoBack(), DeviceInfo.Platform == DevicePlatform.MacCatalyst ? "[" : "Left"));
        view.Add(Item("Forward", () => ActiveView()?.GoForward(), DeviceInfo.Platform == DevicePlatform.MacCatalyst ? "]" : "Right"));

        var serversMenu = new MenuBarItem { Text = "Servers" };
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            var unread = _poller.UnreadFor(p.Id);
            serversMenu.Add(Item(p.Name + (unread > 0 ? $" ({unread})" : "") + (p.Id == _activeId ? "  ✓" : ""), () => _ = OpenProfileAsync(p.Id), i < 9 ? (i + 1).ToString() : null));
        }
        if (profiles.Count == 0) serversMenu.Add(new MenuFlyoutItem { Text = "No servers yet", IsEnabled = false });

        var workspaces = new MenuBarItem { Text = "Workspaces" };
        workspaces.Add(Item("Workspace Ports...", () => _ = ShowWorkspacePortsAsync(), "P", shift: true));

        var notes = new MenuBarItem { Text = "Notifications" };
        notes.Add(Item("Check Now", _poller.PollSoon));
        notes.Add(Item(_store.Settings().NotificationsEnabled ? "Turn Notifications Off" : "Turn Notifications On", () => { _store.SetNotificationsEnabled(!_store.Settings().NotificationsEnabled); _poller.PollSoon(); RebuildMenu(); }));

        MenuBarItems.Clear();
        MenuBarItems.Add(file);
        MenuBarItems.Add(view);
        MenuBarItems.Add(serversMenu);
        MenuBarItems.Add(workspaces);
        MenuBarItems.Add(notes);
    }

    private async Task SwitchServerAsync()
    {
        var profiles = _store.List();
        if (profiles.Count == 0) { ShowServers(); return; }
        var labels = profiles.Select((p, i) => $"{i + 1}. {p.Name}").ToArray();
        var choice = await DisplayActionSheet("Switch server", "Cancel", null, labels);
        var index = Array.IndexOf(labels, choice);
        if (index >= 0) await OpenProfileAsync(profiles[index].Id);
    }

    private async Task RegisterLinksAsync()
    {
        var error = LinkRegistration.Register();
        await DisplayAlert("bytebench:// links", error is null ? "Registered for your Windows user. Links open the newest copy of the app you start from this location." : "Could not register: " + error, "OK");
    }

    // ---------------------------------------------------------------- workspace ports

    private readonly Dictionary<string, IReadOnlyList<string>> _capabilities = [];

    private async Task<(int? Status, string? Body)> ApiAsync(HttpMethod method, ServerProfile p, string path, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(method, new Uri(new Uri(p.Origin), path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Post) req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, _cts.Token);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(_cts.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, null); }
    }

    /// <summary>
    /// Workspaces → Workspace Ports: lists the running workspaces' forwarded ports and opens one in the app (the window navigates to the workspace's own
    /// origin) or in the system browser. The list and the single-use entry link are validated in <see cref="WorkspacePorts"/> before anything is opened.
    /// </summary>
    private async Task ShowWorkspacePortsAsync()
    {
        var profile = _activeId is { } id ? _store.Get(id) : null;
        var view = ActiveView();
        if (profile is null || view is null) { await DisplayAlert("Open a server first", "Workspace ports belong to a ByteBench server. Open one, then choose this command again.", "OK"); return; }
        if (!_capabilities.TryGetValue(profile.Id, out var caps) || !WorkspacePorts.ServerSupportsPorts(caps))
        {
            await DisplayAlert("Workspace ports", "This server is too old to list workspace ports, or it could not be reached. Update ByteBench on the server.", "OK");
            return;
        }
        var token = await _tokens.GetAsync(profile.Id);
        if (token is null)
        {
            await DisplayAlert("Workspace ports", "Add an access token for this server (Servers... > Save token) so the app can list your workspaces' ports.", "OK");
            return;
        }

        var ctx = view.Context;
        var (status, body) = await ApiAsync(HttpMethod.Get, profile, "/api/v1/workspaces", token);
        if (status is not 200) { await DisplayAlert("Workspace ports", status is 401 or 403 ? "The server did not accept the saved access token." : "The workspaces could not be loaded.", "OK"); return; }

        var entries = new List<(WorkspaceSummary W, ForwardedPort P)>();
        foreach (var w in WorkspacePorts.ParseWorkspaces(body))
        {
            var (ps, pb) = await ApiAsync(HttpMethod.Get, profile, $"/api/v1/workspaces/{w.Id}/ports", token);
            if (ps is 200) entries.AddRange(WorkspacePorts.ParsePorts(pb, ctx, w.Id).Select(p => (w, p)));
        }
        if (entries.Count == 0) { await DisplayAlert("Workspace ports", "No running workspace is serving a port. Start your app in a workspace and its ports are listed here.", "OK"); return; }

        var labels = entries.Select(e => WorkspacePorts.Label(e.W, e.P)).ToArray();
        var choice = await DisplayActionSheet("Open a workspace port", "Cancel", null, labels);
        var index = Array.IndexOf(labels, choice);
        if (index < 0) return;
        var (workspace, port) = entries[index];
        var inApp = await DisplayAlert("Open port " + port.Port, "Open it in this window, or in your web browser?", "In the app", "In the browser");

        var (openStatus, openBody) = await ApiAsync(HttpMethod.Post, profile, $"/api/v1/workspaces/{workspace.Id}/ports/{port.Port}/open", token);
        if (openStatus is not 200) { await DisplayAlert("Could not open the port", openStatus == 422 ? "The workspace is not running any more." : "The port could not be opened.", "OK"); return; }
        var url = WorkspacePorts.ParseOpenResponse(openBody, ctx, workspace.Id, port.Port);
        if (url is null) { await DisplayAlert("Could not open the port", "The server answered with an address the app does not trust, so it was not opened.", "OK"); return; }
        if (inApp) view.Navigate(url);
        else ExternalLauncher.Open(url);
    }

    // ---------------------------------------------------------------- deep links

    private async Task HandleDeepLinkAsync(string raw)
    {
        var result = DeepLink.Parse(raw, _store.List());
        if (!result.Ok) { await DisplayAlert("bytebench:// link", result.Reason, "OK"); return; }
        await OpenProfileAsync(result.ProfileId, result.Path);
    }

    // ---------------------------------------------------------------- clone

    private async Task CloneAsync()
    {
        var profile = _activeId is { } id ? _store.Get(id) : null;
        var current = ActiveView()?.CurrentUrl;
        RepoRef? repo = null;
        if (profile is not null && Uri.TryCreate(current, UriKind.Absolute, out var u)) repo = GitClone.RepoFromPath(u.AbsolutePath);
        if (profile is null || repo is null)
        {
            await DisplayAlert("Open a repository first", "Go to a repository page (under Projects, Repositories) and choose this command again.", "OK");
            return;
        }

        var start = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var parent = await DisplayPromptAsync("Clone repository", $"Clone {repo.Key}/{repo.Slug} into which folder? A subfolder named {repo.Slug} is created.", "Clone", "Cancel", initialValue: start);
        if (string.IsNullOrWhiteSpace(parent)) return;

        string? token = await _tokens.GetAsync(profile.Id), user = null;
        if (token is not null) user = (await FetchUserNameAsync(profile, token)).UserName;
        var result = await GitClone.RunAsync(profile.Origin, repo, parent.Trim(), user, token, ct: _cts.Token);
        if (!result.Ok) { await DisplayAlert("Clone failed", result.Message, "OK"); return; }
        try { await Launcher.Default.OpenAsync(new Uri(result.Directory!)); }
        catch (Exception) { await DisplayAlert("Cloned", $"Cloned into {result.Directory}", "OK"); }
    }

    // ---------------------------------------------------------------- small helpers

    private static Label Text(string text, double size, bool bold = false)
    {
        var l = new Label { Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, LineBreakMode = LineBreakMode.WordWrap };
        l.SetAppThemeColor(Label.TextColorProperty, Colors.Black, Colors.White);
        return l;
    }
}
