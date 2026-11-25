using MudBlazor;
using MudBlazor.Utilities;

namespace ByteBench.Components.Layout;

/// <summary>The MudBlazor theme: ByteBench purple, Inter, compact radii, light and dark palettes.</summary>
public static class ByteBenchTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#6d28d9",
            PrimaryLighten = "#8b5cf6",
            PrimaryDarken = "#5b21b6",
            Secondary = "#0ea5e9",
            Tertiary = "#15803d",
            AppbarBackground = "#151322",
            AppbarText = "#ffffff",
            DrawerBackground = "#1e1b2e",
            DrawerText = "#c9c6d9",
            DrawerIcon = "#a7a3bd",
            Background = "#f6f7f9",
            Surface = "#ffffff",
            LinesDefault = "#e3e5ea",
            TableLines = "#e3e5ea",
            TextPrimary = "#1f2328",
            TextSecondary = "#656d76",
            Success = "#15803d",
            Warning = "#b45309",
            Error = "#dc2626",
            Info = "#2563eb",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#a78bfa",
            PrimaryLighten = "#c4b5fd",
            PrimaryDarken = "#8b5cf6",
            Secondary = "#38bdf8",
            Tertiary = "#4ade80",
            AppbarBackground = "#0f0d19",
            AppbarText = "#ffffff",
            DrawerBackground = "#15131f",
            DrawerText = "#c9c6d9",
            DrawerIcon = "#a7a3bd",
            Background = "#0d1117",
            Surface = "#161b22",
            LinesDefault = "#30363d",
            TableLines = "#30363d",
            TextPrimary = "#e6edf3",
            TextSecondary = "#8d96a0",
            Success = "#3fb950",
            Warning = "#d29922",
            Error = "#f85149",
            Info = "#58a6ff",
            // The dark accents are light tints, so filled buttons and chips need dark text (white would be ~2.7:1).
            PrimaryContrastText = "#0d1117",
            SecondaryContrastText = "#0d1117",
            TertiaryContrastText = "#0d1117",
            SuccessContrastText = "#0d1117",
            WarningContrastText = "#0d1117",
            ErrorContrastText = "#0d1117",
            InfoContrastText = "#0d1117",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "-apple-system", "BlinkMacSystemFont", "Segoe UI", "Roboto", "sans-serif"], FontSize = "0.875rem" },
            Button = new ButtonTypography { TextTransform = "none", FontWeight = "500" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", DrawerWidthLeft = "240px" },
    };

    /// <summary>
    /// Dark values for the main surface variables, applied while <c>&lt;html data-bb-dark&gt;</c> is set (wwwroot/js/theme.js sets it
    /// before first paint). Covers the gap before the server-rendered MudThemeProvider matches the resolved theme, for example the
    /// first visit with an OS in dark mode. Generated from <see cref="PaletteDark"/> so the two cannot drift apart.
    /// </summary>
    public static string BootCss { get; } = BuildBootCss();

    private static string BuildBootCss()
    {
        var p = Theme.PaletteDark;
        string C(MudColor c) => c.ToString(MudColorOutputFormats.RGBA);
        return "html[data-bb-dark]{color-scheme:dark;"
            + $"--mud-native-html-color-scheme:dark;--mud-palette-background:{C(p.Background)};--mud-palette-surface:{C(p.Surface)};"
            + $"--mud-palette-text-primary:{C(p.TextPrimary)};--mud-palette-text-secondary:{C(p.TextSecondary)};"
            + $"--mud-palette-lines-default:{C(p.LinesDefault)};--mud-palette-primary:{C(p.Primary)};"
            + $"--mud-palette-drawer-background:{C(p.DrawerBackground)};--mud-palette-appbar-background:{C(p.AppbarBackground)};"
            + "}";
    }
}
