// The only way into Monaco from application code.
//
// `monacoSetup` has to have run before any `<Editor>` mounts — it is what points
// `@monaco-editor/react` at the bundled copy instead of a CDN, and what wires the five language
// workers. That used to be guaranteed by a side-effect import in `main.tsx`, which is also what
// put all of Monaco in the entry chunk: every launch paid for it whether or not an editor was ever
// opened.
//
// Removing that import moves the guarantee here. Importing `./monacoSetup` below means the setup
// travels with the editor rather than with the app, and ES module semantics run it exactly once
// however many components pull it in. `no-restricted-imports` in `eslint.config.js` keeps
// `@monaco-editor/react` from being imported anywhere else, so a new component cannot reintroduce
// the old failure — Monaco silently requested from a CDN in an offline desktop app.

import "./monacoSetup";

export { default as Editor, DiffEditor, type Monaco, type OnMount } from "@monaco-editor/react";
export { monaco, OVERFLOW_SAFE_OPTIONS } from "./monacoSetup";
