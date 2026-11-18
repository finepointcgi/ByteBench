// Roadmap bar dragging (Components/Roadmap/RoadmapView.razor). Bars are <button class="rm-bar" data-id> inside the
// scroll container; dragging the bar moves it, dragging a [data-mode="start"|"end"] handle resizes it. The preview
// snaps to whole days (data-px = pixels per day on the container). On release the component's [JSInvokable] OnDrag
// gets (issueId, mode, days) and saves; a press without movement stays a normal click, which opens the dates dialog.
const THRESHOLD = 4;
const states = new WeakMap();

export function attach(root, dotnet) {
    if (!root || states.has(root)) return;
    let drag = null;
    let suppressClick = false;

    function onDown(e) {
        if (e.button !== 0) return;
        const bar = e.target.closest(".rm-bar[data-edit]");
        if (!bar || !root.contains(bar)) return;
        const handle = e.target.closest("[data-mode]");
        drag = {
            bar,
            id: parseInt(bar.dataset.id, 10),
            mode: handle ? handle.dataset.mode : "move",
            x0: e.clientX,
            left: bar.offsetLeft,
            width: bar.offsetWidth,
            px: parseFloat(root.dataset.px) || 1,
            moved: false,
            pointer: e.pointerId,
        };
        bar.setPointerCapture(e.pointerId);
    }

    function onMove(e) {
        if (!drag || e.pointerId !== drag.pointer) return;
        const dx = e.clientX - drag.x0;
        if (!drag.moved && Math.abs(dx) < THRESHOLD) return;
        drag.moved = true;
        drag.bar.classList.add("rm-dragging");
        const snapped = Math.round(dx / drag.px) * drag.px;
        if (drag.mode === "move") {
            drag.bar.style.left = (drag.left + snapped) + "px";
        } else if (drag.mode === "start") {
            const w = Math.max(drag.px, drag.width - snapped);
            drag.bar.style.left = (drag.left + drag.width - w) + "px";
            drag.bar.style.width = w + "px";
        } else {
            drag.bar.style.width = Math.max(drag.px, drag.width + snapped) + "px";
        }
        e.preventDefault();
    }

    function onUp(e) {
        if (!drag || e.pointerId !== drag.pointer) return;
        const d = drag;
        drag = null;
        try { d.bar.releasePointerCapture(e.pointerId); } catch { /* already released */ }
        d.bar.classList.remove("rm-dragging");
        if (!d.moved) return;
        suppressClick = true;
        const days = Math.round((e.clientX - d.x0) / d.px);
        if (days === 0) {
            d.bar.style.left = d.left + "px";
            d.bar.style.width = d.width + "px";
            return;
        }
        dotnet.invokeMethodAsync("OnDrag", d.id, d.mode, days).catch(() => { });
    }

    function onCancel() {
        if (!drag) return;
        drag.bar.style.left = drag.left + "px";
        drag.bar.style.width = drag.width + "px";
        drag.bar.classList.remove("rm-dragging");
        drag = null;
    }

    // Capture phase on the container runs before Blazor's handlers, so the click that ends a drag never opens the dialog.
    function onClick(e) {
        if (!suppressClick) return;
        suppressClick = false;
        e.stopPropagation();
        e.preventDefault();
    }

    root.addEventListener("pointerdown", onDown);
    root.addEventListener("pointermove", onMove);
    root.addEventListener("pointerup", onUp);
    root.addEventListener("pointercancel", onCancel);
    root.addEventListener("click", onClick, true);
    states.set(root, { onDown, onMove, onUp, onCancel, onClick });
}

export function detach(root) {
    const s = root && states.get(root);
    if (!s) return;
    root.removeEventListener("pointerdown", s.onDown);
    root.removeEventListener("pointermove", s.onMove);
    root.removeEventListener("pointerup", s.onUp);
    root.removeEventListener("pointercancel", s.onCancel);
    root.removeEventListener("click", s.onClick, true);
    states.delete(root);
}

// Scrolls the timeline so that x (pixels from the timeline's left edge) sits a third of the way in.
export function scrollTo(root, x) {
    if (!root) return;
    const left = root.querySelector(".rm-name-head");
    const offset = left ? left.offsetWidth : 0;
    root.scrollLeft = Math.max(0, x - (root.clientWidth - offset) / 3);
}
