using Microsoft.AspNetCore.Http;

namespace ByteBench.Components.Layout;

/// <summary>
/// The colour theme choice (System, Light or Dark). wwwroot/js/theme.js keeps it in localStorage and mirrors it into two
/// cookies so the server can prerender the right palette: <c>bytebench-theme</c> (the choice) and <c>bytebench-scheme</c>
/// (what "system" resolved to on that browser last time). Nothing is stored server-side.
/// </summary>
public static class ThemePreference
{
    public const string System = "system", Light = "light", Dark = "dark";
    public const string ChoiceCookie = "bytebench-theme", SchemeCookie = "bytebench-scheme";

    public static string Normalize(string? value) => value is Light or Dark ? value : System;

    public static string Mode(HttpContext? http) => Normalize(http?.Request.Cookies[ChoiceCookie]);

    /// <summary>Whether the page should render with the dark palette. First-time "system" visitors get light, then theme.js corrects it.</summary>
    public static bool IsDark(HttpContext? http) => Mode(http) switch
    {
        Dark => true,
        Light => false,
        _ => http?.Request.Cookies[SchemeCookie] == Dark,
    };
}
