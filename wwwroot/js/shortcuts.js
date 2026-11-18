// Keyboard shortcuts (the "?" dialog lists them: Components/Layout/ShortcutHelpDialog.razor).
// One document-level keydown listener, no inline handlers (CSP). Keys are ignored while typing in inputs, textareas,
// selects or contenteditable, with modifier keys held, and when the user turned shortcuts off (localStorage).
(function () {
    "use strict";
    const STORAGE_KEY = "gitbench-shortcuts";
    const CHORD_MS = 1200;
    let dotnet = null;       // MainLayout's DotNetObjectReference (interactive pages only)
    let pendingG = 0;        // time "g" was pressed, for the g-chords
    let selected = -1;       // index of the j/k-selected issue row

    function isEnabled() {
        try { return localStorage.getItem(STORAGE_KEY) !== "off"; } catch { return true; }
    }

    function setEnabled(on) {
        try { localStorage.setItem(STORAGE_KEY, on ? "on" : "off"); } catch { }
    }

    function isTyping(e) {
        const t = e.target;
        if (!t || !(t instanceof Element)) return false;
        if (t.isContentEditable) return true;
        const tag = t.tagName;
        if (tag === "TEXTAREA" || tag === "SELECT") return true;
        if (tag === "INPUT") {
            const type = (t.getAttribute("type") || "text").toLowerCase();
            return !["checkbox", "radio", "button", "submit", "reset", "range", "color", "file"].includes(type);
        }
        return !!t.closest("[contenteditable=''], [contenteditable='true'], .mud-popover-open");
    }

    function go(url) {
        if (window.Blazor && typeof window.Blazor.navigateTo === "function") window.Blazor.navigateTo(url);
        else window.location.href = url;
    }

    // "/p/WEB/issues/12" -> "WEB"
    function projectKey() {
        const m = window.location.pathname.match(/^\/p\/([^/]+)/i);
        return m ? m[1].toUpperCase() : null;
    }

    function inProject(suffix) {
        const key = projectKey();
        if (key) go("/p/" + key + suffix);
    }

    function focusSearch() {
        const input = document.querySelector("#gb-global-search input") || document.querySelector(".sr-form input");
        if (!input) return false;
        input.focus();
        if (typeof input.select === "function") input.select();
        return true;
    }

    function issueRows() {
        return Array.from(document.querySelectorAll(".gb-content a.gb-issue-row"));
    }

    function moveSelection(delta) {
        const rows = issueRows();
        if (rows.length === 0) return false;
        const current = rows.findIndex(r => r.classList.contains("gb-kb-selected"));
        selected = current < 0 ? (delta > 0 ? 0 : rows.length - 1) : Math.min(rows.length - 1, Math.max(0, current + delta));
        rows.forEach(r => r.classList.remove("gb-kb-selected"));
        const row = rows[selected];
        row.classList.add("gb-kb-selected");
        row.focus({ preventScroll: true });
        row.scrollIntoView({ block: "nearest" });
        return true;
    }

    function openSelection() {
        const row = issueRows().find(r => r.classList.contains("gb-kb-selected"));
        if (!row) return false;
        row.click();
        return true;
    }

    function clickShortcut(name) {
        const el = document.querySelector("[data-shortcut='" + name + "']");
        if (!el || el.disabled) return false;
        el.click();
        return true;
    }

    function showHelp() {
        if (dotnet) dotnet.invokeMethodAsync("ShowShortcutHelp").catch(() => { });
    }

    function onKeyDown(e) {
        if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
        if (isTyping(e)) return;
        if (document.querySelector(".mud-dialog-container")) return; // a dialog is open
        if (!isEnabled()) return;

        const key = e.key;
        const now = Date.now();
        if (pendingG && now - pendingG < CHORD_MS) {
            pendingG = 0;
            const target = { d: () => go("/"), e: () => go("/explore"), i: () => inProject("/issues"), b: () => inProject("/board"),
                             p: () => inProject("/pull-requests"), r: () => inProject("/repos") }[key];
            if (target) { e.preventDefault(); target(); }
            return;
        }
        pendingG = 0;

        let handled = true;
        switch (key) {
            case "?": showHelp(); break;
            case "/": handled = focusSearch(); break;
            case "g": pendingG = now; break;
            case "c": if (projectKey()) go("/p/" + projectKey() + "/issues/new"); else handled = false; break;
            case "j": handled = moveSelection(1); break;
            case "k": handled = moveSelection(-1); break;
            case "o": handled = openSelection(); break;
            case "Enter": handled = e.target === document.body && openSelection(); break;
            case "e": handled = clickShortcut("edit"); break;
            case "a": handled = clickShortcut("assign-me"); break;
            default: handled = false;
        }
        if (handled) e.preventDefault();
    }

    document.addEventListener("keydown", onKeyDown);

    window.gitbenchShortcuts = {
        attach: (ref) => { dotnet = ref; },
        isEnabled,
        setEnabled,
    };
})();
