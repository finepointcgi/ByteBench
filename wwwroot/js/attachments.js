// Drag-and-drop and paste of files onto a Markdown editor that accepts attachments (<div class="md-editor" data-attach>).
// The files are handed to the editor's hidden Blazor <InputFile> (by setting input.files and firing "change"), so the
// upload itself streams through Blazor like a normal file pick. Blazor then calls gbAttach.insert to put the Markdown
// link at the caret position remembered here.
(function () {
    "use strict";
    if (window.gbAttach) return;

    function editorOf(target) {
        return target && target.closest ? target.closest(".md-editor[data-attach]") : null;
    }

    function hasFiles(dt) {
        return !!dt && Array.prototype.indexOf.call(dt.types || [], "Files") >= 0;
    }

    function forward(root, files) {
        const input = root.querySelector("input[type=file]");
        if (!input || !files || files.length === 0) return;
        const ta = root.querySelector("textarea");
        root.__gbCaret = ta ? [ta.selectionStart, ta.selectionEnd] : null;
        const dt = new DataTransfer();
        for (const f of files) dt.items.add(f);
        input.files = dt.files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
    }

    document.addEventListener("paste", function (e) {
        const root = editorOf(e.target);
        if (!root || !e.clipboardData || e.clipboardData.files.length === 0) return;
        e.preventDefault();
        forward(root, e.clipboardData.files);
    });

    document.addEventListener("dragover", function (e) {
        const root = editorOf(e.target);
        if (!root || !hasFiles(e.dataTransfer)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = "copy";
        root.classList.add("dragging");
    });

    document.addEventListener("dragleave", function (e) {
        const root = editorOf(e.target);
        if (root && !root.contains(e.relatedTarget)) root.classList.remove("dragging");
    });

    document.addEventListener("drop", function (e) {
        const root = editorOf(e.target);
        if (!root || !hasFiles(e.dataTransfer)) return;
        e.preventDefault();
        root.classList.remove("dragging");
        forward(root, e.dataTransfer.files);
    });

    // Remember the caret when the file picker button is used too.
    document.addEventListener("click", function (e) {
        const root = editorOf(e.target);
        if (!root || !e.target.closest(".md-attach")) return;
        const ta = root.querySelector("textarea");
        root.__gbCaret = ta ? [ta.selectionStart, ta.selectionEnd] : null;
    }, true);

    window.gbAttach = {
        // Inserts text at the remembered caret (else at the end) and lets Blazor's @oninput pick up the new value.
        insert: function (textarea, text) {
            if (!textarea) return false;
            const root = editorOf(textarea);
            const caret = root && root.__gbCaret;
            const value = textarea.value;
            const start = caret ? Math.min(caret[0], value.length) : value.length;
            const end = caret ? Math.min(caret[1], value.length) : value.length;
            const before = value.slice(0, start), after = value.slice(end);
            const pre = before.length > 0 && !before.endsWith("\n") ? "\n" : "";
            const post = after.length > 0 && !after.startsWith("\n") ? "\n" : "";
            const insert = pre + text + post;
            textarea.value = before + insert + after;
            const pos = start + insert.length;
            textarea.setSelectionRange(pos, pos);
            if (root) root.__gbCaret = [pos, pos];
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
            textarea.focus();
            return true;
        },
    };
})();
