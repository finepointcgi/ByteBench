// The web editor (Components/Pages/Repositories/WebEditor.razor): Monaco (vendored in /lib/monaco, AMD build), one model
// per open file, auto-save to the server workspace, a diff view, editor shortcuts and the optional inline-completion
// (ghost text) provider. Talks to the page through its DotNetObjectReference:
//   SaveChunk(path, seq, index, count, text) → true when the file is stored; OnDirty(path); OnCommand(name);
//   Complete(path, language, prefix, suffix) → text or null.
// Blazor Server caps messages from the browser (32 KB), so saves are sent in chunks. No inline handlers (CSP).

const BASE = "/lib/monaco/vs";
const SAVE_DELAY_MS = 700;
const CHUNK_CHARS = 5000;
const COMPLETION_DELAY_MS = 400;

let monacoReady = null;
let current = null; // the one editor page instance

function loadScript(src) {
    return new Promise((resolve, reject) => {
        const s = document.createElement("script");
        s.src = src;
        s.async = true;
        s.addEventListener("load", () => resolve());
        s.addEventListener("error", () => reject(new Error("Couldn't load " + src)));
        document.head.appendChild(s);
    });
}

function loadMonaco() {
    if (monacoReady) return monacoReady;
    monacoReady = (async () => {
        if (!window.monaco) {
            if (!window.require || !window.require.config) await loadScript(BASE + "/loader.js");
            // Workers come from the same origin (CSP worker-src 'self'); no blob: workers needed.
            self.MonacoEnvironment = { getWorkerUrl: () => BASE + "/base/worker/workerMain.js" };
            window.require.config({ paths: { vs: BASE, "vs/language/typescript/tsMode": "/js/monaco-tsmode-stub" } });
            await new Promise((resolve, reject) => window.require(["vs/editor/editor.main"], resolve, reject));
        }
        return window.monaco;
    })();
    return monacoReady;
}

function isDark() {
    try {
        const saved = localStorage.getItem("bytebench-theme");
        if (saved) return saved === "dark";
    } catch { }
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

/** The Monaco language id for a path (by file name, then extension), or "plaintext". */
function languageFor(monaco, path) {
    const name = path.split("/").pop().toLowerCase();
    const ext = name.includes(".") ? "." + name.split(".").pop() : "";
    for (const l of monaco.languages.getLanguages()) {
        if ((l.filenames || []).some(f => f.toLowerCase() === name)) return l.id;
    }
    for (const l of monaco.languages.getLanguages()) {
        if (ext && (l.extensions || []).some(e => e.toLowerCase() === ext)) return l.id;
    }
    return "plaintext";
}

function languageName(monaco, id) {
    const l = monaco.languages.getLanguages().find(x => x.id === id);
    return (l && l.aliases && l.aliases[0]) || id;
}

export async function init(host, diffHost, cursorLabel, dotnet, options) {
    const monaco = await loadMonaco();
    if (current) dispose();
    const state = {
        monaco, host, diffHost, cursorLabel, dotnet,
        files: new Map(), // path -> { model, saved: versionId, timer, seq, saving, dirtyReported, readOnly }
        active: null,
        completions: !!(options && options.completions),
        disposables: [],
        diff: null, diffModels: [],
    };
    current = state;
    monaco.editor.setTheme(isDark() ? "vs-dark" : "vs");
    state.editor = monaco.editor.create(host, {
        model: null, automaticLayout: true, minimap: { enabled: true }, fontSize: 13,
        scrollBeyondLastLine: false, renderWhitespace: "selection", tabSize: 4,
        inlineSuggest: { enabled: true }, readOnly: !!(options && options.readOnly),
    });
    const ed = state.editor;
    const K = monaco.KeyMod, C = monaco.KeyCode;
    ed.addCommand(K.CtrlCmd | C.KeyP, () => command("quickOpen"));
    ed.addCommand(K.CtrlCmd | K.Shift | C.KeyP, () => command("commandPalette"));
    ed.addCommand(K.CtrlCmd | K.Shift | C.KeyF, () => command("findInFiles"));
    ed.addCommand(K.CtrlCmd | C.KeyS, () => { if (state.active) flush(state.active); });
    ed.addCommand(K.CtrlCmd | K.Shift | C.KeyL, () => command("focusChat"));
    state.disposables.push(ed.onDidChangeCursorPosition(e => updateCursor(state, e.position)));
    state.disposables.push(ed.onDidChangeCursorSelection(() => updateCursor(state, ed.getPosition())));

    // Shortcuts when the focus is outside Monaco (explorer, panels). Capture phase so the browser's print/save don't fire.
    state.onKey = e => {
        if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
        const k = e.key.toLowerCase();
        let name = null;
        if (k === "p" && e.shiftKey) name = "commandPalette";
        else if (k === "p") name = "quickOpen";
        else if (k === "f" && e.shiftKey) name = "findInFiles";
        else if (k === "s" && !e.shiftKey) { e.preventDefault(); if (state.active) flush(state.active); return; }
        if (!name) return;
        if (ed.hasTextFocus()) return; // Monaco's own command handles it
        e.preventDefault();
        e.stopPropagation();
        command(name);
    };
    document.addEventListener("keydown", state.onKey, true);

    state.disposables.push(monaco.languages.registerInlineCompletionsProvider({ pattern: "**" }, {
        provideInlineCompletions: (model, position, _context, token) => provideCompletion(state, model, position, token),
        freeInlineCompletions: () => { },
        disposeInlineCompletions: () => { },
    }));
    return true;
}

function command(name) {
    if (current) current.dotnet.invokeMethodAsync("OnCommand", name).catch(() => { });
}

function updateCursor(state, pos) {
    if (!state.cursorLabel || !pos) return;
    const sel = state.editor.getSelection();
    let text = `Ln ${pos.lineNumber}, Col ${pos.column}`;
    if (sel && !sel.isEmpty()) {
        const chars = state.editor.getModel()?.getValueInRange(sel).length || 0;
        text += ` (${chars} selected)`;
    }
    state.cursorLabel.textContent = text;
}

/** Opens (or updates) a file. Returns { language, languageName }. */
export function openFile(path, text, readOnly) {
    const state = current;
    if (!state) return null;
    const monaco = state.monaco;
    hideDiff();
    let f = state.files.get(path);
    if (!f) {
        const lang = languageFor(monaco, path);
        const model = monaco.editor.createModel(text ?? "", lang, monaco.Uri.from({ scheme: "bytebench", path: "/" + path }));
        f = { path, model, saved: model.getAlternativeVersionId(), timer: 0, seq: 0, saving: false, dirtyReported: false, readOnly: !!readOnly };
        f.listener = model.onDidChangeContent(() => onChange(state, f));
        state.files.set(path, f);
    } else if (text != null && !isDirty(f) && f.model.getValue() !== text) {
        f.model.setValue(text);
        f.saved = f.model.getAlternativeVersionId();
    }
    f.readOnly = !!readOnly;
    state.active = f;
    state.editor.setModel(f.model);
    state.editor.updateOptions({ readOnly: f.readOnly });
    state.host.style.display = "";
    updateCursor(state, state.editor.getPosition());
    const language = f.model.getLanguageId();
    return { language, languageName: languageName(monaco, language) };
}

/** Replaces a file's text from the server (after accepting an AI proposal, a discard or a rebase). */
export function reloadFile(path, text) {
    const f = current && current.files.get(path);
    if (!f) return;
    clearTimeout(f.timer);
    if (f.model.getValue() !== text) {
        f.model.pushEditOperations([], [{ range: f.model.getFullModelRange(), text }], () => null);
    }
    f.saved = f.model.getAlternativeVersionId();
    f.dirtyReported = false;
}

export function closeFile(path) {
    const state = current;
    const f = state && state.files.get(path);
    if (!f) return;
    flush(f);
    f.listener.dispose();
    if (state.active === f) { state.active = null; state.editor.setModel(null); }
    state.files.delete(path);
    setTimeout(() => f.model.dispose(), 2000); // after a pending save read it
}

/** Renames an open file's model (after a rename in the explorer). */
export function renameFile(from, to) {
    const state = current;
    const f = state && state.files.get(from);
    if (!f) return;
    const text = f.model.getValue();
    closeFile(from);
    openFile(to, text, f.readOnly);
}

export function hideEditor() {
    if (!current) return;
    current.host.style.display = "none";
}

export function setReadOnly(readOnly) {
    if (!current) return;
    for (const f of current.files.values()) f.readOnly = readOnly;
    current.editor.updateOptions({ readOnly });
}

export function setCompletions(enabled) {
    if (current) current.completions = !!enabled;
}

export function revealLine(line) {
    if (!current || !line) return;
    current.editor.revealLineInCenter(line);
    current.editor.setPosition({ lineNumber: line, column: 1 });
    current.editor.focus();
}

export function focusEditor() {
    if (current) current.editor.focus();
}

/** The current selection (text and 1-based lines), or null when nothing is selected. */
export function getSelection() {
    if (!current) return null;
    const sel = current.editor.getSelection();
    const model = current.editor.getModel();
    if (!sel || !model || sel.isEmpty()) return null;
    return { text: model.getValueInRange(sel), startLine: sel.startLineNumber, endLine: sel.endLineNumber };
}

/** Saves every dirty file now; resolves when the server has them. */
export async function flushAll() {
    if (!current) return;
    await Promise.all([...current.files.values()].map(f => flush(f)));
}

// ---------- saving ----------

function isDirty(f) { return f.model.getAlternativeVersionId() !== f.saved; }

function onChange(state, f) {
    if (f.readOnly) return;
    if (!f.dirtyReported && isDirty(f)) {
        f.dirtyReported = true;
        state.dotnet.invokeMethodAsync("OnDirty", f.path).catch(() => { });
    }
    clearTimeout(f.timer);
    f.timer = setTimeout(() => flush(f), SAVE_DELAY_MS);
}

async function flush(f) {
    const state = current;
    if (!state || f.readOnly) return;
    clearTimeout(f.timer);
    if (!isDirty(f)) return;
    if (f.saving) { await f.saving; return flush(f); }
    const version = f.model.getAlternativeVersionId();
    const text = f.model.getValue();
    const seq = ++f.seq;
    const count = Math.max(1, Math.ceil(text.length / CHUNK_CHARS));
    f.saving = (async () => {
        try {
            let ok = false;
            for (let i = 0; i < count; i++) {
                ok = await state.dotnet.invokeMethodAsync("SaveChunk", f.path, seq, i, count, text.substring(i * CHUNK_CHARS, (i + 1) * CHUNK_CHARS));
            }
            if (ok) {
                f.saved = version;
                f.dirtyReported = isDirty(f);
                if (f.dirtyReported) onChange(state, f);
            }
        } catch {
            /* the page shows the error; the file stays dirty and is retried on the next change */
        } finally {
            f.saving = false;
        }
    })();
    await f.saving;
}

// ---------- diff view ----------

/** Shows original vs modified in the diff editor (read-only). */
export function showDiff(path, original, modified) {
    const state = current;
    if (!state) return;
    const monaco = state.monaco;
    if (!state.diff) {
        state.diff = monaco.editor.createDiffEditor(state.diffHost, {
            automaticLayout: true, readOnly: true, originalEditable: false, renderSideBySide: true, fontSize: 13,
            scrollBeyondLastLine: false,
        });
    }
    const lang = languageFor(monaco, path);
    const o = monaco.editor.createModel(original ?? "", lang);
    const m = monaco.editor.createModel(modified ?? "", lang);
    state.diff.setModel({ original: o, modified: m });
    for (const old of state.diffModels) old.dispose();
    state.diffModels = [o, m];
    state.host.style.display = "none";
    state.diffHost.style.display = "";
    return { language: lang, languageName: languageName(monaco, lang) };
}

export function hideDiff() {
    const state = current;
    if (!state || !state.diffHost) return;
    state.diffHost.style.display = "none";
}

// ---------- inline completions (ghost text) ----------

async function provideCompletion(state, model, position, token) {
    if (!state.completions || !state.active || state.active.model !== model || state.active.readOnly) return { items: [] };
    await new Promise(r => setTimeout(r, COMPLETION_DELAY_MS)); // debounce: only ask once typing pauses
    if (token.isCancellationRequested) return { items: [] };
    const full = model.getFullModelRange();
    const prefix = model.getValueInRange({ startLineNumber: 1, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column });
    const suffix = model.getValueInRange({ startLineNumber: position.lineNumber, startColumn: position.column, endLineNumber: full.endLineNumber, endColumn: full.endColumn });
    let text = null;
    try {
        text = await state.dotnet.invokeMethodAsync("Complete", state.active.path, model.getLanguageId(), prefix.slice(-6000), suffix.slice(0, 2000));
    } catch { text = null; }
    if (!text || token.isCancellationRequested) return { items: [] };
    return { items: [{ insertText: text, range: new state.monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column) }] };
}

export function dispose() {
    const state = current;
    if (!state) return;
    current = null;
    document.removeEventListener("keydown", state.onKey, true);
    for (const d of state.disposables) { try { d.dispose(); } catch { } }
    for (const f of state.files.values()) { clearTimeout(f.timer); f.listener.dispose(); f.model.dispose(); }
    for (const m of state.diffModels) m.dispose();
    if (state.diff) state.diff.dispose();
    state.editor.dispose();
}
