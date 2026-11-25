// Colour theme: "system" (default), "light" or "dark". Loaded as a blocking script in <head> so it runs before first paint.
// The choice lives in localStorage and in the "bytebench-theme" cookie; a second cookie, "bytebench-scheme", records the
// resolved light/dark result so the server can prerender the right palette (Components/Layout/ThemePreference.cs reads both).
// <html data-theme="light|dark"> carries the pinned choice (absent = follow the OS) and <html data-bb-dark> marks the
// resolved dark theme, which swaps in the boot colours from Components/Layout/ByteBenchTheme.cs until MudBlazor's own
// palette (MainLayout's MudThemeProvider) has caught up.
(function () {
    "use strict";
    var KEY = "bytebench-theme";
    var SCHEME = "bytebench-scheme";
    var media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    var listener = null; // MainLayout's DotNetObjectReference

    function valid(v) { return v === "light" || v === "dark" ? v : "system"; }

    function readCookie(name) {
        var m = document.cookie.match(new RegExp("(?:^|; )" + name + "=([^;]*)"));
        return m ? decodeURIComponent(m[1]) : null;
    }

    function writeCookie(name, value) {
        try {
            document.cookie = name + "=" + encodeURIComponent(value) + "; path=/; max-age=31536000; SameSite=Lax";
        } catch (e) { }
    }

    function get() {
        try { var v = localStorage.getItem(KEY); if (v) return valid(v); } catch (e) { }
        return valid(readCookie(KEY));
    }

    function resolve(mode) {
        return mode === "dark" || (mode === "system" && !!media && media.matches);
    }

    function apply() {
        var mode = get();
        var dark = resolve(mode);
        var root = document.documentElement;
        if (mode === "system") root.removeAttribute("data-theme"); else root.setAttribute("data-theme", mode);
        if (dark) root.setAttribute("data-bb-dark", ""); else root.removeAttribute("data-bb-dark");
        writeCookie(KEY, mode);
        writeCookie(SCHEME, dark ? "dark" : "light");
        return dark;
    }

    function notify(dark) {
        if (!listener) return;
        try { listener.invokeMethodAsync("OnThemeChanged", get(), dark); } catch (e) { listener = null; }
    }

    // Stores the choice and returns whether the resolved theme is dark.
    function set(mode) {
        mode = valid(mode);
        try { if (mode === "system") localStorage.removeItem(KEY); else localStorage.setItem(KEY, mode); } catch (e) { }
        return apply();
    }

    // MainLayout registers to hear about OS-level and other-tab changes.
    function watch(ref) { listener = ref; }

    window.bytebenchTheme = { get: get, set: set, isDark: function () { return resolve(get()); }, watch: watch };
    apply();

    function onChange() { notify(apply()); }
    if (media) {
        if (media.addEventListener) media.addEventListener("change", onChange); else if (media.addListener) media.addListener(onChange);
    }
    // Another tab changed the theme.
    window.addEventListener("storage", function (e) { if (e.key === KEY) onChange(); });
    // Enhanced navigation can replace the <html> element's attributes: re-apply after each one.
    document.addEventListener("DOMContentLoaded", function () {
        if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener("enhancedload", apply);
    });
})();
