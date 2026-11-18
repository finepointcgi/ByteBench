// Autocomplete for the Markdown editor (Components/Shared/MarkdownEditor.razor): "@na" suggests people who can view the
// project, "#12" / "#login" / "WEB-" suggest issues. Suggestions come from the editor's [JSInvokable] Suggest method
// (auth-checked on the server, max 8 rows), debounced. Up/Down move, Enter/Tab pick, Esc closes; the mouse works too.
// The dropdown is position:fixed at the caret (the editor clips overflow), and built with textContent only.

const DEBOUNCE_MS = 120;
let nextId = 0;

// Token immediately before the caret. Groups: 1 = "@name", 2 = "#query", 3/4 = "KEY-digits".
const TOKEN = /(?:^|[\s(\[{,;:!?"'])(?:@([A-Za-z0-9][A-Za-z0-9._+-]{0,49})|#([A-Za-z0-9][^\s#@]{0,49})|([A-Z][A-Z0-9]{1,9})-(\d{0,9}))$/;

export function attach(root, dotnet) {
    if (!root || root.__gbAc) return;
    const state = { root, dotnet, box: null, items: [], active: 0, token: null, timer: 0, seq: 0, id: "gb-ac-" + (++nextId) };
    root.__gbAc = state;

    const on = (el, type, fn, opts) => { el.addEventListener(type, fn, opts); (state.off ??= []).push(() => el.removeEventListener(type, fn, opts)); };
    on(root, "input", e => { if (isTextarea(e.target) && !e.isComposing) schedule(state, e.target); });
    on(root, "keydown", e => onKeyDown(state, e), true);
    on(root, "click", e => { if (isTextarea(e.target)) schedule(state, e.target); });
    on(root, "focusout", e => { if (isTextarea(e.target)) setTimeout(() => { if (document.activeElement !== e.target) close(state); }, 150); });
    on(window, "scroll", () => reposition(state), true);
    on(window, "resize", () => reposition(state));
}

export function detach(root) {
    const state = root && root.__gbAc;
    if (!state) return;
    close(state);
    clearTimeout(state.timer);
    (state.off || []).forEach(f => f());
    delete root.__gbAc;
}

function isTextarea(el) { return el && el.tagName === "TEXTAREA"; }

function currentToken(ta) {
    if (ta.selectionStart !== ta.selectionEnd) return null;
    const caret = ta.selectionStart;
    const before = ta.value.slice(Math.max(0, caret - 60), caret);
    const m = TOKEN.exec(before);
    if (!m) return null;
    const text = m[0].replace(/^[\s(\[{,;:!?"']/, "");
    const start = caret - text.length;
    if (m[1] !== undefined) return { kind: "user", key: null, query: m[1], start, end: caret };
    if (m[2] !== undefined) return { kind: "issue", key: null, query: m[2], start, end: caret };
    return { kind: "issue", key: m[3], query: m[4], start, end: caret };
}

function schedule(state, ta) {
    clearTimeout(state.timer);
    const token = currentToken(ta);
    if (!token) { close(state); return; }
    state.timer = setTimeout(() => lookup(state, ta, token), DEBOUNCE_MS);
}

async function lookup(state, ta, token) {
    const seq = ++state.seq;
    let items = [];
    try { items = await state.dotnet.invokeMethodAsync("Suggest", token.kind, token.key, token.query) || []; }
    catch { items = []; }
    if (seq !== state.seq || document.activeElement !== ta) return; // stale, or the user moved on
    const now = currentToken(ta);
    if (!now || now.start !== token.start) { close(state); return; }
    state.token = now;
    state.items = items;
    state.active = 0;
    if (items.length === 0) close(state); else render(state, ta);
}

function onKeyDown(state, e) {
    if (!state.box || !isTextarea(e.target) || e.isComposing) return;
    switch (e.key) {
        case "ArrowDown": move(state, 1); break;
        case "ArrowUp": move(state, -1); break;
        case "Enter": case "Tab": pick(state, e.target, state.active); break;
        case "Escape": close(state); break;
        default: return;
    }
    e.preventDefault();
    e.stopPropagation();
}

function move(state, delta) {
    const n = state.items.length;
    state.active = (state.active + delta + n) % n;
    highlight(state);
}

function pick(state, ta, index) {
    const item = state.items[index];
    const token = currentToken(ta);
    if (!item || !token) { close(state); return; }
    const value = ta.value;
    const after = value.slice(token.end);
    const insert = item.insert + (after.startsWith(" ") ? "" : " ");
    ta.value = value.slice(0, token.start) + insert + after;
    const pos = token.start + insert.length;
    ta.setSelectionRange(pos, pos);
    close(state);
    ta.dispatchEvent(new Event("input", { bubbles: true })); // Blazor's @oninput picks up the new value
    ta.focus();
    clearTimeout(state.timer); // the synthetic input must not reopen the list
}

function render(state, ta) {
    if (!state.box) {
        const box = document.createElement("div");
        box.className = "gb-ac";
        box.id = state.id;
        box.setAttribute("role", "listbox");
        box.addEventListener("mousedown", e => e.preventDefault()); // keep focus in the textarea
        document.body.appendChild(box);
        state.box = box;
        state.ta = ta;
        ta.setAttribute("aria-autocomplete", "list");
        ta.setAttribute("aria-controls", state.id);
        ta.setAttribute("aria-expanded", "true");
    }
    const box = state.box;
    box.replaceChildren();
    state.items.forEach((item, i) => {
        const row = document.createElement("div");
        row.className = "gb-ac-item";
        row.id = state.id + "-" + i;
        row.setAttribute("role", "option");
        if (item.initials !== null && item.initials !== undefined) {
            const av = document.createElement("span");
            av.className = "gb-ac-avatar";
            av.textContent = item.initials;
            if (item.color) av.style.background = item.color;
            row.appendChild(av);
        } else {
            const icon = document.createElement("span");
            icon.className = "gb-ac-icon";
            icon.textContent = "#";
            row.appendChild(icon);
        }
        const label = document.createElement("span");
        label.className = "gb-ac-label";
        label.textContent = item.label;
        row.appendChild(label);
        if (item.detail) {
            const detail = document.createElement("span");
            detail.className = "gb-ac-detail";
            detail.textContent = item.detail;
            row.appendChild(detail);
        }
        row.addEventListener("mouseenter", () => { state.active = i; highlight(state); });
        row.addEventListener("click", () => pick(state, ta, i));
        box.appendChild(row);
    });
    highlight(state);
    reposition(state);
}

function highlight(state) {
    if (!state.box) return;
    Array.from(state.box.children).forEach((row, i) => {
        const on = i === state.active;
        row.classList.toggle("active", on);
        row.setAttribute("aria-selected", on ? "true" : "false");
        if (on) row.scrollIntoView({ block: "nearest" });
    });
    state.ta && state.ta.setAttribute("aria-activedescendant", state.id + "-" + state.active);
}

function close(state) {
    state.seq++;
    if (state.box) { state.box.remove(); state.box = null; }
    if (state.ta) {
        state.ta.setAttribute("aria-expanded", "false");
        state.ta.removeAttribute("aria-activedescendant");
        state.ta = null;
    }
    state.items = [];
}

function reposition(state) {
    const ta = state.ta, box = state.box;
    if (!ta || !box || !state.token) return;
    if (!ta.isConnected) { close(state); return; }
    const c = caretCoordinates(ta, state.token.start);
    const r = ta.getBoundingClientRect();
    const lineHeight = parseFloat(getComputedStyle(ta).lineHeight) || 20;
    let top = r.top + c.top - ta.scrollTop + lineHeight + 2;
    let left = r.left + c.left - ta.scrollLeft;
    const w = box.offsetWidth, h = box.offsetHeight;
    if (left + w > window.innerWidth - 8) left = Math.max(8, window.innerWidth - w - 8);
    if (top + h > window.innerHeight - 8) top = Math.max(8, r.top + c.top - ta.scrollTop - h - 2); // flip above the caret
    box.style.top = top + "px";
    box.style.left = left + "px";
}

// Pixel position of character `index` inside a textarea, via a hidden mirror div with the same text metrics.
const MIRROR_PROPS = ["boxSizing", "width", "height", "overflowX", "overflowY", "borderTopWidth", "borderRightWidth",
    "borderBottomWidth", "borderLeftWidth", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft", "fontStyle",
    "fontVariant", "fontWeight", "fontStretch", "fontSize", "lineHeight", "fontFamily", "textAlign", "textTransform",
    "textIndent", "letterSpacing", "wordSpacing", "tabSize"];

function caretCoordinates(ta, index) {
    const div = document.createElement("div");
    const style = getComputedStyle(ta);
    MIRROR_PROPS.forEach(p => { div.style[p] = style[p]; });
    div.style.position = "absolute";
    div.style.visibility = "hidden";
    div.style.whiteSpace = "pre-wrap";
    div.style.overflowWrap = "break-word";
    div.style.top = "0";
    div.style.left = "-9999px";
    div.textContent = ta.value.slice(0, index);
    const span = document.createElement("span");
    span.textContent = ta.value.slice(index) || ".";
    div.appendChild(span);
    document.body.appendChild(div);
    const coords = {
        top: span.offsetTop + parseFloat(style.borderTopWidth || "0"),
        left: span.offsetLeft + parseFloat(style.borderLeftWidth || "0"),
    };
    div.remove();
    return coords;
}
