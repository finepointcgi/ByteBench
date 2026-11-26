// Enter inside an element marked data-enter-no-submit (e.g. the issue picker) must not submit the surrounding form:
// browsers submit on keydown before Blazor's handlers run. Only the default is cancelled, so the component still sees
// the key (MudAutocomplete picks the highlighted item). A file, not an inline handler, so the production CSP allows it.
document.addEventListener("keydown", e => {
    if (e.key === "Enter" && e.target instanceof HTMLInputElement && e.target.closest("[data-enter-no-submit]")) e.preventDefault();
}, true);

// Inputs inside an element marked data-select-on-focus (e.g. the clone URL box) select their text on focus,
// so the URL can be copied in one go. A file, not an inline handler, so the production CSP allows it.
document.addEventListener("focusin", e => {
    if ((e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) && e.target.closest("[data-select-on-focus]")) e.target.select();
});

// <button data-copy-from="id"> copies that element's value (inputs) or text (anything else) and says "Copied". Delegated, so it works on
// server-rendered pages without a Blazor circuit, and it runs inside the click's user gesture, which the clipboard API needs.
// <button data-confirm="question"> asks before the click goes on (a form's submit button, a link).
document.addEventListener("click", e => {
    const target = e.target instanceof Element ? e.target : null;
    const confirmer = target && target.closest("[data-confirm]");
    if (confirmer && !window.confirm(confirmer.getAttribute("data-confirm"))) { e.preventDefault(); e.stopPropagation(); return; }
    const copier = target && target.closest("[data-copy-from]");
    if (!copier) return;
    const source = document.getElementById(copier.getAttribute("data-copy-from"));
    if (!source || !navigator.clipboard) return;
    const text = "value" in source ? source.value : source.textContent;
    navigator.clipboard.writeText(text).then(() => { copier.textContent = "Copied"; }, () => { });
}, true);
