// Remembers the light/dark choice per browser (MainLayout reads it on first render).
window.gitbenchTheme = {
    get: () => { try { return localStorage.getItem("gitbench-theme"); } catch { return null; } },
    set: (v) => { try { localStorage.setItem("gitbench-theme", v); } catch { } },
};
