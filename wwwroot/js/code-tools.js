// Syntax highlighting and linkable line numbers for ByteBench (uses the vendored highlight.js in lib/highlightjs).
//
// Blazor owns the DOM, so this never touches nodes Blazor tracks:
//  - Code lines are rendered as MarkupString "<span class=hl-line>text</span>". Blazor tracks the span and replaces
//    it whole when the line changes; we only rewrite the span's children.
//  - Markdown is one MarkupString; we rewrite the children of its <pre><code> elements.
// A MutationObserver re-runs the (idempotent) pass whenever Blazor renders, so highlighting survives re-renders,
// enhanced navigation and live updates without any per-component interop.
(function () {
    "use strict";
    if (!window.hljs || window.bytebenchCodeTools) return;
    window.bytebenchCodeTools = true;
    hljs.configure({ ignoreUnescapedHTML: true });

    const MAX_CHARS = 200 * 1024;
    const DONE = "__gbHl"; // expando on elements we've highlighted (not an attribute, so it isn't diffed or observed)

    function langOk(lang) {
        return !!lang && lang !== "plaintext" && !!hljs.getLanguage(lang);
    }

    // Splits highlight.js HTML into lines, closing open spans at each newline and reopening them on the next line.
    function splitLines(html) {
        const out = [];
        const open = [];
        let cur = "";
        const re = /<span[^>]*>|<\/span>|\n|[^<\n]+/g;
        let m;
        while ((m = re.exec(html)) !== null) {
            const t = m[0];
            if (t === "\n") {
                out.push(cur + "</span>".repeat(open.length));
                cur = open.join("");
            } else if (t.startsWith("<span")) {
                open.push(t);
                cur += t;
            } else if (t === "</span>") {
                open.pop();
                cur += t;
            } else {
                cur += t;
            }
        }
        out.push(cur);
        return out;
    }

    // Highlights a list of .hl-line spans as one continuous document (so multi-line comments and strings work).
    function highlightLines(lines, lang) {
        if (lines.length === 0) return;
        const text = lines.map(l => l.textContent).join("\n");
        if (text.length > MAX_CHARS) return;
        let html;
        try { html = hljs.highlight(text, { language: lang, ignoreIllegals: true }).value; }
        catch { return; }
        const parts = splitLines(html);
        if (parts.length !== lines.length) return; // shouldn't happen; leave plain rather than misalign
        for (let i = 0; i < lines.length; i++) {
            lines[i].innerHTML = parts[i];
            lines[i][DONE] = true;
        }
    }

    function highlightContainer(container) {
        const lang = container.getAttribute("data-hl-lang");
        const all = Array.from(container.querySelectorAll(".hl-line"));
        if (all.length === 0 || all.every(l => l[DONE])) return;
        if (!langOk(lang)) { all.forEach(l => l[DONE] = true); return; }
        if (container.hasAttribute("data-hl-diff")) {
            // Diff: highlight the old side (context + deleted) and the new side (context + added) separately.
            // Context lines are in both; the new side runs last, so they end up with its colouring.
            const is = (l, cls) => !!l.closest("tr")?.classList.contains(cls);
            if (all.some(l => is(l, "diff-deleted"))) highlightLines(all.filter(l => !is(l, "diff-added")), lang);
            highlightLines(all.filter(l => !is(l, "diff-deleted")), lang);
            all.forEach(l => l[DONE] = true);
        } else {
            highlightLines(all, lang);
        }
    }

    function highlightMarkdown(code) {
        if (code[DONE]) return;
        code[DONE] = true;
        const m = /(?:^|\s)language-([\w#+.-]+)/.exec(code.className);
        const lang = m && m[1].toLowerCase();
        if (!langOk(lang) || code.textContent.length > MAX_CHARS) return;
        try {
            code.innerHTML = hljs.highlight(code.textContent, { language: lang, ignoreIllegals: true }).value;
            code.classList.add("hljs");
        } catch { /* leave plain */ }
    }

    // ----- #L42 / #L10-L20 line links in the file viewer -----

    let appliedFor = null;
    let anchorLine = null;

    function parseHash(hash) {
        const m = /^#L(\d+)(?:-L?(\d+))?$/.exec(hash || "");
        if (!m) return null;
        const a = +m[1], b = m[2] ? +m[2] : a;
        return [Math.min(a, b), Math.max(a, b)];
    }

    function applyLineSelection(force) {
        const table = document.querySelector("table.code-table[data-linkable]");
        if (!table) { appliedFor = null; return; }
        const key = location.href;
        if (!force && appliedFor === key && table[DONE + "Sel"]) return;
        table.querySelectorAll("tr.line-selected").forEach(r => r.classList.remove("line-selected"));
        const range = parseHash(location.hash);
        table[DONE + "Sel"] = true;
        appliedFor = key;
        if (!range) return;
        let first = null;
        for (let n = range[0]; n <= range[1]; n++) {
            const row = document.getElementById("L" + n);
            if (row && table.contains(row)) { row.classList.add("line-selected"); first = first || row; }
        }
        if (first && !force) first.scrollIntoView({ block: "center" });
    }

    document.addEventListener("click", e => {
        const link = e.target instanceof Element ? e.target.closest("a.ln-link") : null;
        if (!link || e.button !== 0 || e.ctrlKey || e.metaKey || e.altKey) return;
        // Handle it here so Blazor's router doesn't treat it as a navigation (the <base href> would make "#L1" go home).
        e.preventDefault();
        e.stopPropagation();
        const n = +link.getAttribute("data-line");
        const current = parseHash(location.hash);
        let hash = "#L" + n;
        if (e.shiftKey && (anchorLine || current)) {
            const a = anchorLine || current[0];
            hash = a === n ? "#L" + n : "#L" + Math.min(a, n) + "-L" + Math.max(a, n);
        } else {
            anchorLine = n;
        }
        history.replaceState(history.state, "", location.pathname + location.search + hash);
        applyLineSelection(true);
    }, true);

    // ----- driver -----

    let scheduled = false;
    function run() {
        scheduled = false;
        document.querySelectorAll("[data-hl-lang]").forEach(highlightContainer);
        document.querySelectorAll(".markdown pre > code").forEach(highlightMarkdown);
        applyLineSelection(false);
    }
    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(run, 0); // not requestAnimationFrame: that never fires in hidden tabs, leaving them unhighlighted
    }

    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener("popstate", schedule);
    schedule();
})();
