// Remembers the light/dark choice per browser (MainLayout reads it on first render).
window.bytebenchTheme = {
    get: () => { try { return localStorage.getItem("bytebench-theme"); } catch { return null; } },
    set: (v) => { try { localStorage.setItem("bytebench-theme", v); } catch { } },
};
