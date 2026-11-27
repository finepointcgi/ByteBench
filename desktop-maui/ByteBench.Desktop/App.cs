namespace ByteBench.Desktop;

/// <summary>Code-only application: one window with <see cref="MainPage"/> (no XAML resources to maintain).</summary>
public sealed class App : Application
{
    private readonly MainPage _page;

    public App(MainPage page)
    {
        _page = page;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_page) { Title = "ByteBench", Width = 1280, Height = 840, MinimumWidth = 720, MinimumHeight = 480 };
}
