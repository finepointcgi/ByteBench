using MudBlazor;

namespace GitBench.Components.Layout;

/// <summary>The MudBlazor theme: GitBench purple, Inter, compact radii, light and dark palettes.</summary>
public static class GitBenchTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#6d28d9",
            PrimaryLighten = "#8b5cf6",
            PrimaryDarken = "#5b21b6",
            Secondary = "#0ea5e9",
            Tertiary = "#16a34a",
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
            Success = "#16a34a",
            Warning = "#d97706",
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
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "-apple-system", "BlinkMacSystemFont", "Segoe UI", "Roboto", "sans-serif"], FontSize = "0.875rem" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", DrawerWidthLeft = "240px" },
    };
}
