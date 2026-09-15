# 15 — Schema designer

## Scope

- `src/CodeFlow.App/Dbml/` — `DbmlCommands.cs`, `DbmlDocuments.cs`, `DbmlLayoutStore.cs`,
  `DbmlTablePosition.cs`, `DbmlJsonContext.cs`
- `renderer/src/lib/dbml/` — `parse.ts` (the `@dbml/core` boundary), `model.ts`, `layout.ts`,
  `schema.ts` (the Editor preview's adapter), `documentPath.ts`
- `renderer/src/state/dbmlStore.ts`
- `renderer/src/components/dbml/` — `DbmlView.tsx`, `NewDbmlModal.tsx`

A workbench for [DBML](https://dbml.dbdiagram.io) documents: the `.dbml` files of the open folder,
an editor, and the entity diagram the text produces, updated as it is typed.

**The split of responsibility is the shape of this feature.** A schema document is a file in the
user's folder, so opening, saving and creating one are `read_file_text`, `write_file_text` and
`create_file` — commands that already exist and that need no repository. Parsing, layout and
rendering all live in the renderer, where `@dbml/core` is. What the sidecar owns is the one thing
the renderer cannot do: walking a folder for documents.

That leaves `renderer/src/components/editor/DbmlPreview.tsx` (the `.dbml` preview inside the Editor
module) standing. It shares `lib/dbml/schema.ts` and `components/editor/DbmlDiagram.tsx` with this
module rather than duplicating either, and stays as the quick look at a document you already have
open in the file tree.

## Commands

Contract (parameters, return types) is `01-ipc-surface.md`'s `src/CodeFlow.App/Dbml/DbmlCommands.cs`
table. One line each:

- `dbml_list_documents` — every `.dbml` file under a folder, project-relative and sorted.
- `dbml_load_layout` — the positions a person gave one document's tables.
- `dbml_save_positions` — stores positions, moving any table that already had one.
- `dbml_clear_layout` — forgets one document's positions, so the auto-layout places all of it again.

## The `@dbml/core` boundary

`renderer/src/lib/dbml/parse.ts` is the only module that imports the parser. It is 15 MB minified
— four times Monaco — so everything that reaches it sits behind a `lazy()`: `DbmlView` in
`App.tsx`, and `DbmlPreview` in `EditorPane.tsx`. The production build puts it in a chunk of its
own, which is the arrangement to preserve; importing `schema.ts` from anything eager undoes it.

The parser does not throw plain `Error`s. Invalid DBML raises a `CompilerError` shaped as
`{ diags: [...] }`, so `String(e)` and `e.message` both produce `[object Object]`; `formatParseError`
unpacks it into the positioned `message (line:column)` the editor shows. That unpacking is pinned by
`schema.test.ts`, which exists because it broke across the 8→9 major bump.

## Rules

### DBML-001 A document is found by walking the folder, not by asking git
**Implementation**: `src/CodeFlow.App/Dbml/DbmlDocuments.cs`
**Behaviour**: `dbml_list_documents` walks `rootPath` for files ending in `.dbml`, compared
case-insensitively, and returns them project-relative, sorted, with `/` separators on every
platform. It prunes rather than filters: a directory in `PrunedDirectories` (`.git`, `node_modules`,
`bin`, `obj`, `dist`, `build`, `out`, `target`, `.venv`, `venv`, `__pycache__`, `vendor`, `.next`,
`.nuxt`, `.svelte-kit`, `.gradle`, `.idea`, `.vs`, `Pods`, `DerivedData`) is never read from disk.
**Inputs / outputs**: `rootPath: string` → `Result<Vec<string>, string>`.
**Edge cases**: capped at `MaxDocuments` (2 000) and `MaxDepth` (24). Symlinked directories are
skipped, not followed — depth alone bounds a loop, but a link pointing back up the tree would report
the same file under two paths, and each is a distinct layout key. A directory that cannot be read is
skipped rather than failing the listing. A missing `rootPath` throws `no such folder: {path}`.
**Frontend dependency**: `renderer/src/lib/ipc/commands.ts` (`dbmlListDocuments`),
`renderer/src/state/dbmlStore.ts`.
**Markers**: none. It does **not** reuse `Files/RepoWalk.cs`, which is the obvious thing to reach
for: that one takes an open `LibGit2Sharp.Repository` because it prunes through
`Repository.Ignore.IsPathIgnored`, and a schema designer has to work in a plain folder (`GIT-039`).
The fixed prune list is the substitute for gitignore rules, and it means a `.dbml` under
`node_modules` — a dependency's, not the user's — is never offered.

---

### DBML-002 The buffer is the source of truth while a document is open
**Implementation**: `renderer/src/state/dbmlStore.ts` · `renderer/src/components/dbml/DbmlView.tsx`
**Behaviour**: Opening a document reads it into `source` and marks it clean, and loads its stored positions in
parallel (`DBML-005`); a layout that cannot be read still opens the document, auto-laid out, with the
failure reported once. The diagram is parsed
from `source` on every change, not from disk, which is what makes it live. `save` writes the buffer
through `write_file_text` and clears `dirty` — **comparing against the text that was written**, not
against the current buffer, so an edit made while the write was in flight stays dirty instead of
being silently lost at the next document switch. `Mod+S` inside the editor saves.
**Inputs / outputs**: internal store state; the IO is `read_file_text` / `write_file_text`, both
repo-scoped through `PathGuards.ResolveWithinRepo` with the project folder as the root.
**Edge cases**: a read that resolves after the user picked another document is discarded, the same
guard `repoStore.setRepoPath` applies to its refreshes. A read that fails closes the document rather
than leaving an empty one open under its name. Saving a clean buffer writes nothing. The store is
reset when the selected project changes, because the module is repo-scoped.
**Frontend dependency**: none outward; this is renderer-internal.
**Markers**: none.

---

### DBML-003 A new document's name is validated before anything is written
**Implementation**: `renderer/src/lib/dbml/documentPath.ts` · `renderer/src/components/dbml/NewDbmlModal.tsx`
**Behaviour**: `normalizeDocumentPath` appends `.dbml` when it is missing (so "orders" and
"orders.dbml" name one file), normalises `\` to `/`, collapses repeated and trailing separators, and
refuses four things by discriminated reason rather than by message: `empty`, `absolute`, `escapes`
(any `.` or `..` segment) and `invalidChar` (`< > : " | ? *`, and any control character). The store
adds `exists`, compared **case-insensitively** because macOS and Windows both are. A created
document starts with a one-table starter schema, not empty.
**Inputs / outputs**: `string` → `{ ok: true, relPath } | { ok: false, reason }`.
**Edge cases**: a name that is only the extension (`.dbml`) is `empty`. A lone `/` is `absolute`,
which is the more useful of the two things to report. A Windows drive letter (`C:/x`) is caught by
the `:` in the invalid-character set.
**Frontend dependency**: the reasons are keys into `renderer/src/lib/i18n/translations.ts`
(`dbml.error.*`), both locales.
**Markers**: none. The validation is duplicated with `PathGuards.ResolveNewPath` on purpose: the
sidecar's refusal is correct but arrives as a raw error string, while this one arrives as a labelled
field under the input. The sidecar's guard stays the boundary; this one is the message.

---

### DBML-004 The module is repo-scoped but needs no repository
**Implementation**: `renderer/src/lib/modules.ts` · `renderer/src/components/layout/ContextPanel.tsx`
**Behaviour**: `dbml` is registered with `scope: "repo"` — it follows the selected project and
reloads when it changes — and deliberately **without** `requiresGit`, so it stays available in a
plain folder (`GIT-039`). Its context panel is `null`: the document picker is in its own toolbar,
and the column beside it is where the diagram needs the width. Shortcut `Mod+5` (`view.dbml`).
**Inputs / outputs**: none.
**Edge cases**: with no project selected the view renders its empty state rather than an error.
**Frontend dependency**: adding the registry entry forces `MODULE_VIEWS` (`App.tsx`) and
`MODULE_PANEL` (`ContextPanel.tsx`) to gain a `dbml` key or stop compiling — the coupling
`lib/modules.ts` documents.
**Markers**: none.

---

### DBML-005 Positions persist per document, and outlive their tables
**Implementation**: `src/CodeFlow.App/Dbml/DbmlLayoutStore.cs` · `src/CodeFlow.App/Dbml/DbmlCommands.cs` ·
`src/CodeFlow.App/Storage/Schema.cs` (`dbml_layouts`)
**Behaviour**: Only positions a person set are stored, one row per `(project_id, rel_path,
table_key)`; every other table is placed by the auto-layout (`DBML-007`) each render. A save is a
**single multi-row `INSERT … ON CONFLICT`**, so it is atomic without a transaction: one that fails
leaves the previous layout intact rather than half of the new one. A key repeated within one save
keeps its last position. `dbml_clear_layout` removes one document's rows; the cascade from `projects`
removes a project's.
**Inputs / outputs**: `dbml_load_layout(projectId, relPath)` → `[{ table_key, x, y }]`, ordered by
`table_key`. `dbml_save_positions(projectId, relPath, positions: [{ table_key, x, y }])` → `null`.
`dbml_clear_layout(projectId, relPath)` → `null`. The position objects keep their snake_case keys in
both directions — they are rows sent back, the exception `ApiCommands` documents.
**Edge cases**: an empty save writes nothing. A position with a blank `table_key` refuses the whole
save with `a position is missing its table_key` and writes nothing. More than `MaxPositions` (2 000)
is refused. A save for a project that does not exist fails on the foreign key, surfaced as SQLite's
own `FOREIGN KEY constraint failed`. **Rows are not pruned** for tables the document no longer
declares: renaming a table and undoing the rename must not cost its position, and the layout ignores
keys it has no table for. Moving a document within the project changes `rel_path` and so loses its
layout; the auto-layout places it again.
**Frontend dependency**: `renderer/src/lib/ipc/commands.ts` (`dbmlLoadLayout`, `dbmlSavePositions`,
`dbmlClearLayout`), `renderer/src/types/domain.ts` (`DbmlTablePosition`).
**Markers**: none. Owned table documented in `03-storage.md`.

---

### DBML-006 One parser, one model
**Implementation**: `renderer/src/lib/dbml/parse.ts` · `renderer/src/lib/dbml/model.ts` · `renderer/src/lib/dbml/schema.ts`
**Behaviour**: `parseDbmlModel` turns DBML text into plain data: tables keyed `schema.table` in lower
case (unqualified ones filed under `public`), columns with their written type (arguments included),
`pk`, `notNull`, `unique`, `increment`, default and note, indexes, references whose two ends carry
`relation` `1` or `*` plus `onDelete`/`onUpdate`, and enums. `schema.ts` is an adapter narrowing
that model to the Editor preview's older shape — there is one walk of the parser's output, not two.
**Inputs / outputs**: `string` → `{ ok: true, model } | { ok: false, error }`.
**Edge cases**: blank input returns an empty model without invoking the parser. Invalid DBML returns
the positioned message from `formatParseError`, never a throw. A default written as an expression
keeps its backticks, so it cannot pass for a string literal. `users` and `public.users` produce the
same key, so a stored position does not depend on how a reference spelled the table.
**Frontend dependency**: `components/editor/DbmlPreview.tsx` (through `schema.ts`),
`components/dbml/DbmlView.tsx`.
**Markers**: none.

---

### DBML-007 The auto-layout separates, and never covers what a person placed
**Implementation**: `renderer/src/lib/dbml/layout.ts`
**Behaviour**: A reduced Sugiyama. Tables split into connected components; a table's column is the
length of its longest foreign-key chain, so a referenced table always sits left of the tables that
point at it (`<` reverses the written direction; `-` and `<>` keep it). Each column is reordered by
the barycenter of its neighbours over four sweeps to reduce crossings, and centred against the
tallest. Larger components come first; tables with no relationship go to a square grid underneath.
Card size is derived from the model — width 260, height `36 + max(1, columns) × 26 + 8` — which is
what lets the layout run without a DOM. Gaps are deliberately generous: 160 between columns, 56
between stacked cards, 120 between components.
**Inputs / outputs**: `(model, pinned: Map<table_key, {x, y}>, options?)` → `Map<table_key, rect>`.
**Edge cases**: a pinned table keeps exactly its stored coordinates; every free table is then pushed
down, in reading order, past any card closer than the row gap — so a table added to the document
lands in free space. A cycle is broken where it is met, deterministically. A pin for a table the
document no longer declares places nothing. The same model always produces the same picture.
**Frontend dependency**: the schema canvas.
**Markers**: none.

---

### DBML-008 The canvas moves only what is being dragged, and keeps the last picture that parsed
**Implementation**: `renderer/src/components/dbml/DbmlCanvas.tsx` · `renderer/src/lib/dbml/viewport.ts` ·
`renderer/src/components/dbml/DbmlView.tsx` · `renderer/src/state/dbmlStore.ts` (`placeTable`, `arrangeAll`)
**Behaviour**: Cards are drawn at the rectangles `computeLayout` returns, from the same geometry
constants. A press on a card's header becomes a drag past `DRAG_THRESHOLD`; **during the drag only
that card moves**, and the layout is recomputed once, on drop — recomputing on every move would let
the push-down rule (`DBML-007`) shove other cards around under the cursor. The drop goes through
`placeTable`, which rounds to whole pixels, updates memory and persists; a save that fails keeps the
card where it was dropped and reports it. The header is a focusable button: arrow keys nudge the table
16 px, 64 px with Shift — the keyboard route to what a drag does. Dragging the background or scrolling
pans; Ctrl/Cmd + wheel zooms around the cursor, between 0.2× and 2×, through a native non-passive
listener, because React registers `onWheel` as passive and could not stop the window from zooming
instead. The view fits itself once per document, and a fit never zooms past 1×. While the buffer does
not parse, the canvas keeps the last model that did and shows the positioned error over it, so typing
does not blank the diagram or reset the zoom. "Arrange automatically" asks for confirmation only when
the document has positions set by hand.
**Inputs / outputs**: none on the wire beyond `DBML-005`.
**Edge cases**: a zoom keeps the diagram point under the cursor fixed (asserted in `viewport.test.ts`).
Fitting an empty diagram or a zero-sized view yields the identity view. A document with no tables shows
an empty state instead of a canvas.
**Frontend dependency**: none outward.
**Markers**: none. Not unit-tested as a component — the renderer's Vitest runs without a DOM — which is
why the arithmetic lives in `viewport.ts`, `layout.ts` and `edges.ts`; the drag itself is verified in
the running app.

---

### DBML-009 A relationship line attaches to the column it names
**Implementation**: `renderer/src/lib/dbml/edges.ts`
**Behaviour**: Each reference whose two tables are on the canvas becomes one segment, anchored at the
vertical centre of its column's row — `header + index × row + row / 2` — and leaving each card from the
side that faces the other. Reading which column references which is the point; a line to the middle of
the card, which is what the Editor preview draws, cannot say it.
**Inputs / outputs**: `(refs, tables by key, rects by key)` → `[{ id, from: {x, y}, to: {x, y} }]`.
**Edge cases**: a column the card does not show anchors at the header. A reference to a table that is
not on the canvas draws nothing.
**Frontend dependency**: `DbmlCanvas.tsx`.
**Markers**: none. Known limit, stated rather than discovered: these are straight segments, so a
self-reference crosses its own card and lines can cross cards between two columns. Orthogonal routing
and the hover that explains a relationship in words replace this in the next phase.

## Test coverage

| Test | Source | Kind |
|---|---|---|
| `DbmlCommandsTests` (20) | `src/CodeFlow.App/Dbml/` | scenario — real temp directories and a real migrated database |
| `MigrationTests` (table and index counts) | `src/CodeFlow.App/Storage/Schema.cs` | scenario |
| `parse.test.ts` (8) | `renderer/src/lib/dbml/parse.ts` | boundary over `@dbml/core` |
| `schema.test.ts` (3) | `renderer/src/lib/dbml/schema.ts` | adapter smoke |
| `layout.test.ts` (14) | `renderer/src/lib/dbml/layout.ts` | pure — invariants, never pixels |
| `edges.test.ts` (4) | `renderer/src/lib/dbml/edges.ts` | pure |
| `viewport.test.ts` (5) | `renderer/src/lib/dbml/viewport.ts` | pure |
| `documentPath.test.ts` (12) | `renderer/src/lib/dbml/documentPath.ts` | pure |
| `dbmlStore.test.ts` (18) | `renderer/src/state/dbmlStore.ts` | store, `lib/ipc/commands` mocked |

The view itself is not tested: the renderer's Vitest runs `environment: "node"` with no jsdom and no
testing-library, so component behaviour is covered by extracting its logic — which is why
`documentPath.ts` is a module and not a function inside the modal.

## Markers raised

None yet.
