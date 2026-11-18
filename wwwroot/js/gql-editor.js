// The GQL editor (Components/Query/GqlEditor.razor): autocomplete for fields, operators and values, and Enter to run.
// Suggestions come from the component's [JSInvokable] Complete(text, caret) (server-side, scoped to what the user can see),
// debounced. Up/Down move, Enter/Tab pick, Esc closes; Enter with no list open (or Ctrl/Cmd+Enter) runs the query.
// The dropdown reuses the .gb-ac styles of the Markdown editor's autocomplete and is built with textContent only.

const DEBOUNCE_MS = 100;
let nextId = 0;

export function attach(root, dotnet) {
    const ta = root && root.querySelector("textarea, input");
    if (!ta || root.__gql) return;
    const state = { root, ta, dotnet, box: null, items: [], active: 0, start: 0, end: 0, timer: 0, seq: 0, id: "gb-gql-" + (++nextId) };
    root.__gql = state;
    ta.setAttribute("spellcheck", "false");
    ta.setAttribute("autocomplete", "off");
    ta.setAttribute("role", "combobox");

    const on = (el, type, fn, opts) => { el.addEventListener(type, fn, opts); (state.off ??= []).push(() => el.removeEventListener(type, fn, opts)); };
    on(ta, "input", e => { if (!e.isComposing) schedule(state); });
    on(ta, "focus", () => schedule(state));
    on(ta, "click", () => schedule(state));
    on(ta, "keyup", e => { if (e.key === "ArrowLeft" || e.key === "ArrowRight" || e.key === "Home" || e.key === "End") schedule(state); });
    on(ta, "keydown", e => onKeyDown(state, e), true);
    on(ta, "blur", () => setTimeout(() => { if (document.activeElement !== ta) close(state); }, 150));
    on(window, "scroll", () => reposition(state), true);
    on(window, "resize", () => reposition(state));
}

export function detach(root) {
    const state = root && root.__gql;
    if (!state) return;
    close(state);
    clearTimeout(state.timer);
    (state.off || []).forEach(f => f());
    delete root.__gql;
}

/** Puts the caret at the end (after the component replaced the text, e.g. "Switch to advanced"). */
export function focusEnd(root) {
    const state = root && root.__gql;
    if (!state) return;
    state.ta.focus();
    const n = state.ta.value.length;
    state.ta.setSelectionRange(n, n);
}

function schedule(state) {
    clearTimeout(state.timer);
    if (state.quietUntil && Date.now() < state.quietUntil) return;
    state.timer = setTimeout(() => lookup(state), DEBOUNCE_MS);
}

async function lookup(state) {
    const ta = state.ta;
    if (document.activeElement !== ta || ta.selectionStart !== ta.selectionEnd) { close(state); return; }
    const seq = ++state.seq;
    const text = ta.value, caret = ta.selectionStart;
    let result = null;
    try { result = await state.dotnet.invokeMethodAsync("Complete", text, caret); } catch { result = null; }
    if (seq !== state.seq || document.activeElement !== ta || ta.value !== text || ta.selectionStart !== caret) return; // stale
    if (!result || !result.items || result.items.length === 0) { close(state); return; }
    state.items = result.items;
    state.start = result.start;
    state.end = result.end;
    state.active = 0;
    render(state);
}

function onKeyDown(state, e) {
    if (e.isComposing) return;
    if (state.box) {
        switch (e.key) {
            case "ArrowDown": move(state, 1); break;
            case "ArrowUp": move(state, -1); break;
            case "Tab": pick(state, state.active); break;
            case "Enter":
                if (e.ctrlKey || e.metaKey) { close(state); run(state); }
                else pick(state, state.active);
                break;
            case "Escape": close(state); break;
            default: return;
        }
    } else if (e.key === "Enter" && !e.shiftKey) {
        run(state);
    } else if (e.key === " " && e.ctrlKey) {
        schedule(state);
    } else {
        return;
    }
    e.preventDefault();
    e.stopPropagation();
}

function run(state) {
    clearTimeout(state.timer);
    close(state);
    state.quietUntil = Date.now() + 500; // the re-render after a run shouldn't reopen the list
    state.dotnet.invokeMethodAsync("Run", state.ta.value).catch(() => { });
}

function move(state, delta) {
    const n = state.items.length;
    state.active = (state.active + delta + n) % n;
    highlight(state);
}

function pick(state, index) {
    const item = state.items[index];
    const ta = state.ta;
    if (!item) { close(state); return; }
    const value = ta.value;
    const after = value.slice(state.end);
    const opensList = item.insert.endsWith("(");
    const insert = item.insert + (opensList || after.startsWith(" ") ? "" : " ");
    ta.value = value.slice(0, state.start) + insert + after;
    const pos = state.start + insert.length;
    ta.setSelectionRange(pos, pos);
    close(state);
    ta.dispatchEvent(new Event("input", { bubbles: true })); // Blazor's binding picks up the new value
    ta.focus();
    schedule(state); // offer what comes next (an operator after a field, values after an operator…)
}

function render(state) {
    const ta = state.ta;
    if (!state.box) {
        const box = document.createElement("div");
        box.className = "gb-ac gb-gql-ac";
        box.id = state.id;
        box.setAttribute("role", "listbox");
        box.addEventListener("mousedown", e => e.preventDefault()); // keep focus in the editor
        document.body.appendChild(box);
        state.box = box;
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
        const kind = document.createElement("span");
        kind.className = "gb-gql-kind gb-gql-kind-" + (item.kind || "value");
        kind.textContent = { field: "F", operator: "op", keyword: "K", function: "ƒ" }[item.kind] || "V";
        row.appendChild(kind);
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
        row.addEventListener("click", () => pick(state, i));
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
    state.ta.setAttribute("aria-activedescendant", state.id + "-" + state.active);
}

function close(state) {
    state.seq++;
    if (state.box) { state.box.remove(); state.box = null; }
    state.ta.setAttribute("aria-expanded", "false");
    state.ta.removeAttribute("aria-activedescendant");
    state.items = [];
}

function reposition(state) {
    const ta = state.ta, box = state.box;
    if (!box) return;
    if (!ta.isConnected) { close(state); return; }
    const c = caretCoordinates(ta, state.start);
    const r = ta.getBoundingClientRect();
    const lineHeight = parseFloat(getComputedStyle(ta).lineHeight) || 20;
    let top = r.top + c.top - ta.scrollTop + lineHeight + 2;
    let left = r.left + c.left - ta.scrollLeft;
    const w = box.offsetWidth, h = box.offsetHeight;
    if (left + w > window.innerWidth - 8) left = Math.max(8, window.innerWidth - w - 8);
    if (top + h > window.innerHeight - 8) top = Math.max(8, r.top + c.top - ta.scrollTop - h - 2);
    box.style.top = top + "px";
    box.style.left = left + "px";
}

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
