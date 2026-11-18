// Auto-scroll for the pipeline log viewer: follows new output while the reader is at the bottom,
// and stops following as soon as they scroll up (resumes when they scroll back down).
export function init(el) {
    if (!el || el.dataset.ciInit) return;
    el.dataset.ciInit = "1";
    el.dataset.follow = "true";
    el.addEventListener("scroll", () => {
        el.dataset.follow = (el.scrollHeight - el.scrollTop - el.clientHeight < 40) ? "true" : "false";
    });
    el.scrollTop = el.scrollHeight;
}

export function follow(el, force) {
    if (!el) return;
    if (force) el.dataset.follow = "true";
    if (el.dataset.follow !== "false") el.scrollTop = el.scrollHeight;
}
