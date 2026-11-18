// Colour theme: "system" (default), "light" or "dark", stored in localStorage. Loaded as a blocking script in <head>
// so the theme is applied before first paint (no flash of the light theme).
// <html data-theme="light|dark"> pins a theme (absent = follow the OS, see wwwroot/css/theme.css), and
// data-bs-theme carries the resolved theme for Bootstrap's own components.
(function () {
    var KEY = "gitbench-theme";
    var media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    function get() {
        try {
            var v = localStorage.getItem(KEY);
            return v === "light" || v === "dark" ? v : "system";
        } catch (e) { return "system"; }
    }

    function apply() {
        var mode = get();
        var root = document.documentElement;
        var dark = mode === "dark" || (mode === "system" && !!media && media.matches);
        if (mode === "system") root.removeAttribute("data-theme"); else root.setAttribute("data-theme", mode);
        root.setAttribute("data-bs-theme", dark ? "dark" : "light");
    }

    function set(mode) {
        try {
            if (mode === "light" || mode === "dark") localStorage.setItem(KEY, mode); else localStorage.removeItem(KEY);
        } catch (e) { /* storage blocked: the choice lasts for this page only */ }
        apply();
    }

    window.gitbenchTheme = { get: get, set: set, apply: apply };
    apply();

    if (media) {
        if (media.addEventListener) media.addEventListener("change", apply); else if (media.addListener) media.addListener(apply);
    }
    // Another tab changed the theme.
    window.addEventListener("storage", function (e) { if (e.key === KEY) apply(); });
    // Enhanced navigation can replace the <html> element's attributes: re-apply after each one.
    document.addEventListener("DOMContentLoaded", function () {
        if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener("enhancedload", apply);
    });
})();
