// Stand-in for Monaco's TypeScript language service (vs/language/typescript/tsMode), which ByteBench doesn't vendor (~5.8 MB).
// TypeScript/JavaScript keep Monarch syntax highlighting from vs/basic-languages; only IntelliSense/diagnostics are absent.
// Mapped in wwwroot/js/web-editor.js through the AMD loader's `paths`.
define(["exports"], function (exports) {
    "use strict";
    exports.setupTypeScript = function () { };
    exports.setupJavaScript = function () { };
    exports.getTypeScriptWorker = function () { return Promise.reject(new Error("The TypeScript language service isn't installed.")); };
    exports.getJavaScriptWorker = exports.getTypeScriptWorker;
});
