// Enter inside an element marked data-enter-no-submit (e.g. the issue picker) must not submit the surrounding form:
// browsers submit on keydown before Blazor's handlers run. Only the default is cancelled, so the component still sees
// the key (MudAutocomplete picks the highlighted item). A file, not an inline handler, so the production CSP allows it.
document.addEventListener("keydown", e => {
    if (e.key === "Enter" && e.target instanceof HTMLInputElement && e.target.closest("[data-enter-no-submit]")) e.preventDefault();
}, true);
