# 15 — Schema designer

## Scope

- `src/CodeFlow.App/Dbml/` — `DbmlCommands.cs`, `DbmlDocuments.cs`, `DbmlLayoutStore.cs`,
  `DbmlAssistant.cs`, `DbmlTablePosition.cs`, `DbmlJsonContext.cs`, `DbmlConnection.cs`,
  `DbmlConnectionStore.cs`, `DbmlSnapshot.cs`, `DbmlSnapshotBuilder.cs`, `IDbmlIntrospector.cs`,
  `DbmlIntrospection.cs`
- `src/CodeFlow.App/Dbml/Introspectors/` — `Catalogue.cs`, `PostgresIntrospector.cs`,
  `SqlServerIntrospector.cs`, `MySqlIntrospector.cs`, `SqliteIntrospector.cs`
- `src/CodeFlow.App/Ai/Prompts/` — `DBML_EDIT_PROMPT.txt`, `DBML_REVIEW_PROMPT.txt`,
  `DBML_EXPLAIN_PROMPT.txt`
- `renderer/src/lib/dbml/` — `parse.ts` (the `@dbml/core` boundary), `model.ts`, `layout.ts`,
  `edges.ts`, `routing.ts`, `inflect.ts`, `relationPhrase.ts`, `viewport.ts`, `documentPath.ts`,
  `assist.ts`, `emitDbml.ts`, `connectionError.ts`
- `renderer/src/lib/dbml/exporters/` — `sql.ts`, `prisma.ts`
- `renderer/src/lib/dbml/importers/` — `sql.ts`, `prisma.ts`
- `renderer/src/state/dbmlStore.ts`
- `renderer/src/components/dbml/` — `DbmlView.tsx`, `DbmlCanvas.tsx`, `DbmlViewportControls.tsx`,
  `NewDbmlModal.tsx`, `ExportDbmlModal.tsx`, `ImportDbmlModal.tsx`, `DbmlAiModal.tsx`,
  `DbConnectionPanel.tsx`
- `renderer/src/components/editor/DbmlPreview.tsx` — the Editor's quick look, drawn by the same
  canvas

A workbench for [DBML](https://dbml.dbdiagram.io) documents: the `.dbml` files of the open folder,
an editor, and the entity diagram the text produces, updated as it is typed.

**The split of responsibility is the shape of this feature.** A schema document is a file in the
user's folder, so opening, saving and creating one are `read_file_text`, `write_file_text` and
`create_file` — commands that already exist and that need no repository. Parsing, layout and
rendering all live in the renderer, where `@dbml/core` is. What the sidecar owns is the one thing
the renderer cannot do: walking a folder for documents.

`renderer/src/components/editor/DbmlPreview.tsx` (the `.dbml` preview inside the Editor module)
stays as the quick look at a document you already have open in the file tree, and **draws with this
module's canvas** — see `DBML-018`.

## Commands

Contract (parameters, return types) is `01-ipc-surface.md`'s `src/CodeFlow.App/Dbml/DbmlCommands.cs`
table. One line each:

- `dbml_list_documents` — every `.dbml` file under a folder, project-relative and sorted.
- `dbml_load_layout` — the positions a person gave one document's tables.
- `dbml_save_positions` — stores positions, moving any table that already had one.
- `dbml_clear_layout` — forgets one document's positions, so the auto-layout places all of it again.
- `dbml_assist` — runs one of three AI modes over the document's text.
- `dbml_list_connections` — the saved databases. Never carries a password.
- `dbml_save_connection` — creates or updates one; a blank password leaves the stored one alone.
- `dbml_delete_connection` — removes it, and the password with it.
- `dbml_test_connection` — opens the connection and closes it.
- `dbml_introspect_database` — reads the schema, as structured data rather than as DBML.

## The `@dbml/core` boundary

`renderer/src/lib/dbml/parse.ts` is the only module that imports the parser. It is 15 MB minified
— four times Monaco — so everything that reaches it sits behind a `lazy()`: `DbmlView` in
`App.tsx`, and `DbmlPreview` in `EditorPane.tsx`. **The invariant is that it stays out of the eager
`index` chunk**, and importing `parse.ts` from anything eager is what would undo it.

It is not a chunk of its own: both lazy entries need the parser, so Rollup hoists it into the chunk
they share, along with whatever else those two have in common. That chunk takes its name from
whichever module Rollup picks — at the time of writing, `DbmlViewportControls`, which is thirty
lines. The name is cosmetic and the size in the build output is the parser. `vite.config.ts`
deliberately declares no `manualChunks`, so this is the arrangement to read rather than one to pin.

**It parses with the `dbmlv2` grammar**, not the `dbml` one — see `DBML-019`. Both are in the
package; the second is the compiler that replaced the first, and it is a superset.

The parser does not throw plain `Error`s. Invalid DBML raises a `CompilerError` shaped as
`{ diags: [...] }`, so `String(e)` and `e.message` both produce `[object Object]`; `formatParseError`
unpacks it into the positioned `message (line:column)` the editor shows. That unpacking is pinned by
`parse.test.ts`, which carries it because it broke across the 8→9 major bump.

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
**Implementation**: `renderer/src/lib/dbml/edges.ts` (`columnAnchorY`)
**Behaviour**: A line meets a card at the vertical centre of the row of the column its reference names —
`header + index × row + row / 2`. Reading which column references which is the point; a line to the
middle of the card, which is what the Editor preview draws, cannot say it. `DBML-012` builds the line
from these anchors.
**Inputs / outputs**: `(rect, table, column name)` → `y`.
**Edge cases**: a column the card does not show anchors at the header. A composite reference anchors at
its first column.
**Frontend dependency**: `renderer/src/lib/dbml/routing.ts`.
**Markers**: none.

---

### DBML-010 Table names become nouns in the language they are written in
**Implementation**: `renderer/src/lib/dbml/inflect.ts`
**Behaviour**: `splitWords` reads snake, kebab, camel and Pascal case alike. Singular and plural follow
Spanish or English rules plus short exception lists; Spanish gender comes from the ending plus
exception lists. `nounFor` keeps a name that already reads as plural as its own plural (`animal_vacunas`
stays "animal vacunas"); English inflects only the last word, its head; Spanish singularises every word
and takes the gender from the first. `guessLanguage` scores the schema's table **and column** names for
Spanish and English markers and falls back to the interface language on a tie — the nouns must follow
the names, since Spanish rules never turn `users` into "user".
**Inputs / outputs**: identifier + `"es" | "en"` → `{ singular, plural, gender }`.
**Edge cases**: a Spanish `-e` singular after a consonant cluster takes only `-s` (`detalles`, `nombres`,
`clientes`); `-iones` singularises with its accent (`canciones` → "canción"); a final-syllable stress
mark falls away in the plural (`almacén` → "almacenes").
**Frontend dependency**: `renderer/src/lib/dbml/relationPhrase.ts`, `DbmlCanvas.tsx`.
**Markers**: none. Heuristic by design — table names are a narrow vocabulary, and a wrong guess costs an
awkward word in a tooltip, never a wrong diagram. Some words take the rule's answer rather than the
right one (`meses` → "mese", `sedes` → "sed").

---

### DBML-011 Every relationship reads in both directions
**Implementation**: `renderer/src/lib/dbml/relationPhrase.ts` · `renderer/src/lib/i18n/translations.ts` (`dbml.relation.*`)
**Behaviour**: One-to-many (`>` or `<`): "Cada {uno} puede tener muchos {muchos}" and "Cada {muchos}
pertenece a un {uno}" — or "puede pertenecer a" when any foreign-key column is nullable and not a
primary key. One-to-one (`-`): the side written first belongs to the other, which has at most one of it.
Many-to-many (`<>`): "puede relacionarse con muchos" in both directions. The quantity word agrees with
its noun's gender through keys of its own (`un`/`una`, `muchos`/`muchas`; English uses "one"/"many" for
both), so the wording stays in `translations.ts` while this module only chooses the sentence and
inflects its nouns.
**Inputs / outputs**: `(ref, tables by key, names language)` → `{ kind, sentences: [two] }`, rendered
through the caller's translator.
**Edge cases**: a foreign-key column the table does not declare is treated as required — claiming an
optionality the document does not state would be the worse mistake. A reference to a table not in the
document yields nothing.
**Frontend dependency**: `DbmlCanvas.tsx`.
**Markers**: none.

---

### DBML-012 Relationship lines are orthogonal and never run through the two cards they join
**Implementation**: `renderer/src/lib/dbml/routing.ts`
**Behaviour**: A line leaves its card horizontally at the anchor, turns once into a vertical lane and
enters the other card horizontally. When the cards are at least `2 × STUB` (48) apart horizontally the
lane is in the gap and the ends are the facing sides; otherwise — overlapping horizontally, too close to
turn, or a table referencing itself — both ends leave on the right and the lane runs outside both cards.
Lines sharing a gap are spread 12 px apart around its centre, ordered by their starting row so
neighbours do not swap and cross; lanes outside stack outward. Corners are rounded (radius 8, never more
than half a segment). Each end carries its cardinality outside the card: a bar at a `1`, a crow's foot
at a `*`.
**Inputs / outputs**: `(refs, tables by key, rects by key)` → `[{ id, from, to, points, d, label, markers }]`.
**Edge cases**: rows that line up give a single straight segment. A reference to a table not on the
canvas draws nothing.
**Frontend dependency**: `DbmlCanvas.tsx`.
**Markers**: none. Known limit: a lane between two cards can pass behind a third card placed inside that
gap by hand. Cards are drawn above lines, so the line is hidden there rather than drawn over the card.

---

### DBML-013 A relationship explains itself on hover or focus, and is silent at rest
**Implementation**: `renderer/src/components/dbml/DbmlCanvas.tsx`
**Behaviour**: Each line has an invisible 14 px hit stroke. With the pointer over it — or keyboard focus
on it, since every line is in the tab order and labelled with its two sentences for screen readers — the
line thickens, the others dim, the two tables it joins take the accent border, and a tooltip shows
`from.columns → to.columns`, both sentences (`DBML-011`) and the `ON DELETE` / `ON UPDATE` actions when
the reference declares them. Nothing is shown at rest. The tooltip follows the pointer; for keyboard
focus it sits at the middle of the line's lane. Nouns are inflected in the schema's guessed language;
the sentences around them follow the interface language.
**Inputs / outputs**: none on the wire.
**Edge cases**: no tooltip appears while a card is being dragged or the background panned. The tooltip is
kept inside the canvas, flipping above the pointer near the bottom edge. A reference edited away while
its tooltip is open closes it.
**Frontend dependency**: none outward.
**Markers**: none.

---

### DBML-014 SQL export is delegated, and an empty result is a failure
**Implementation**: `renderer/src/lib/dbml/exporters/sql.ts`
**Behaviour**: PostgreSQL and SQL Server come from `@dbml/core` itself: parse the buffer, hand the
model to `ModelExporter.export`, return the SQL with a trailing newline. Invalid DBML raises the same
positioned message the canvas shows, before any dialog opens.
**Inputs / outputs**: `(source, "postgres" | "mssql")` → SQL text; throws otherwise.
**Edge cases**: an empty document is refused. A result that is blank is refused too — **`prisma` is
not routed through here for exactly that reason**: `ModelExporter.export(db, "prisma")` does not
throw for a target it does not know, it returns an empty string, which would be written out as a
successful export of nothing.
**Frontend dependency**: `components/dbml/ExportDbmlModal.tsx`.
**Markers**: none.

---

### DBML-015 The Prisma schema is written here, and never guesses silently
**Implementation**: `renderer/src/lib/dbml/exporters/prisma.ts`
**Behaviour**: Emitted from the canonical model for one of two providers (`postgresql`,
`sqlserver`). Types map per provider (`varchar(n)` → `@db.VarChar(n)` or `@db.NVarChar(n)`, `text` →
`@db.Text` or `@db.NVarChar(Max)`, `uuid` → `@db.Uuid` or `@db.UniqueIdentifier`, numeric precision
kept, and so on). `increment` becomes `@default(autoincrement())`; `now()`-shaped expressions become
`@default(now())`, UUID generators `@default(uuid())`, anything else `@default(dbgenerated(...))`.
Both ends of every relation are written, which DBML does not have: the foreign-key side carries
`@relation(fields:, references:)` with the referential actions, the other side gains the list — or,
for one-to-one, the optional single field. A many-to-many becomes Prisma's implicit form: a list on
each side and no foreign key. Named schemas are declared (`schemas`, `previewFeatures`, `@@schema`)
rather than silently collapsing every table into `public`.
**Inputs / outputs**: `(model, provider)` → Prisma schema text.
**Edge cases**: **a composite primary key arrives as an index with `pk` set, not as flagged
columns** — reading the column flag alone is what left a join table's columns optional and its key
missing, so the key is resolved from both. A foreign key inside the primary key is required whatever
its `not null` says. Two references between the same pair of tables, and any self-reference, get a
relation name, without which Prisma cannot tell them apart. A relation field whose name a column
already uses is suffixed. A type with no Prisma equivalent becomes `String` under a
`/// TODO:` line naming the SQL type; an expression index becomes a `///` note.
**Frontend dependency**: `components/dbml/ExportDbmlModal.tsx`.
**Markers**: none. The inverse field names come from `inflect.ts` (`DBML-010`), so they inherit its
heuristics — an awkward plural in a schema is a field name, never a wrong relation.

---

### DBML-016 The assistant is one command with three modes, and reaches for nothing
**Implementation**: `src/CodeFlow.App/Dbml/DbmlAssistant.cs` · `src/CodeFlow.App/Ai/Prompts/DBML_*_PROMPT.txt`
**Behaviour**: `dbml_assist` takes `mode`, the document's text, and an instruction. The mode selects
one of three embedded system prompts and nothing else: `edit` is asked for the whole document
rewritten, with no prose and no fence; `review` judges the design; `explain` describes it. The two
that are read answer in Spanish, like every other AI answer the app shows. The ask rides on argv and
the schema on stdin (`AI-002`), and only `edit`'s reply goes through `StripCodeFence` — stripping a
review's first fenced block would eat a finding.

Routing is its own task key, `dbml`, so a schema can be pointed at a different model than a code
review (`XLANG-004`). The toolset is bound to the **empty list**, which the engines read as "no tools
at all": the model is handed the whole document and asked about that document, so a tool call could
only re-read what it already has or wander into a folder that need not be a repository. A user who
has set a toolset for the provider in Settings keeps it.
**Inputs / outputs**: `(mode, dbml, instruction?, runId?)` → DBML text for `edit`, Spanish markdown
otherwise.
**Edge cases**: an empty document is refused. A schema over 60 000 characters is refused rather than
truncated — `edit` is asked to return the whole document, so a dropped tail would come back as a
proposal that deletes every table past the cut. `edit` with a blank instruction is refused, because
applying nothing has no meaning; the other two stand on their own. The instruction is capped at
4 000 characters, by Unicode scalar. An unknown mode names itself in the error, which is what a
renderer/backend drift looks like from a log.
**Frontend dependency**: `components/dbml/DbmlAiModal.tsx`, `lib/dbml/assist.ts`.
**Markers**: none.

---

### DBML-017 A proposal is parsed before it is offered, and applied only to the buffer
**Implementation**: `renderer/src/lib/dbml/assist.ts` · `renderer/src/components/dbml/DbmlAiModal.tsx`
**Behaviour**: **Nothing the model returns is trusted to be DBML.** An `edit` reply is run through
the same parser the canvas uses (`DBML-006`) before the dialog offers it, so an engine that answers
with an apology, a fragment or half a document produces a rejected proposal rather than a corrupted
schema. `checkProposal` answers with one of four states — `ok`, `invalid`, `empty`, `unchanged` —
modelled as a union, so a proposal that must not be applied is not representable as one that can.

Accepting puts the text in the **editor buffer**, never on disk: the save button and Ctrl+Z keep
owning it, the same bargain `InlineEditWidget` makes. Review and explain answers are rendered as
sanitised markdown and can be applied to nothing.
**Inputs / outputs**: `(current, answer)` → `DbmlProposal`.
**Edge cases**: a reply identical to the document after normalising line endings and trailing
whitespace is `unchanged`, not an edit — the prompt tells the model to return the schema untouched
when it cannot apply the instruction, and offering that would produce a whitespace-only change. An
**open document that does not parse is not an error**: that is the state the editor is in for most
of an edit, and the proposal is still offerable with every table reading as added. The table delta
is shown above the diff because a model that obeys the format and returns three tables of seven has
lost the document in a way only a scrolled diff would reveal; a non-empty `removed` list is painted
as a danger chip. The rejected answer is shown verbatim under the error, so "it was rejected" and
"here is what it said" are not the same screen.
**Frontend dependency**: none outward.
**Markers**: none.

---

### DBML-018 One diagram, drawn in both places it appears
**Implementation**: `renderer/src/components/editor/DbmlPreview.tsx` · `renderer/src/components/dbml/DbmlCanvas.tsx`
**Behaviour**: The Editor's `.dbml` preview and the schema module draw with the **same** canvas. There
used to be two: `components/editor/DbmlDiagram.tsx` laid cards out in a `flex-wrap` and joined their
*centres* with straight dashed lines, over a second adapter (`lib/dbml/schema.ts`) that narrowed the
parser's model to what it drew. So the same file looked different depending on which door it was
opened through — no auto-layout, no column anchors, no relationship phrases — and a fix to the
diagram reached only one of them. Both files are gone, and with them their adapter's test, whose two
assertions `parse.test.ts` already made.

The shared zoom/fit cluster is `DbmlViewportControls`, whose `onArrange` is optional: re-arranging
means forgetting positions a person saved, and the preview has none.
**Inputs / outputs**: `(content, path)` → the diagram. `path` is the canvas's `documentKey`, so
switching tabs re-fits.
**Edge cases**: the preview's drag is **ephemeral** — persistence is keyed on a project and a
document (`DBML-005`) and belongs to the schema module, which is the surface that has both. Nothing
is lost by it: `computeLayout` is deterministic, so reopening the file gives the same picture back.
The preview takes **no scroll ref** in split mode, unlike the Markdown one: the diagram is a pan/zoom
surface, not a vertical rendering of the text beside it, and syncing a scroll ratio to it moved the
picture for no reason a reader could connect to the line they were on.
**Frontend dependency**: `components/editor/EditorPane.tsx`.
**Markers**: none.

---

### DBML-019 The document is parsed with `dbmlv2`, and the old grammar was a ceiling
**Implementation**: `renderer/src/lib/dbml/parse.ts` (`GRAMMAR`)
**Behaviour**: `@dbml/core` ships two grammars. `dbml` is the original PEG parser; `dbmlv2` is the
compiler that replaced it, and it is a **superset** — everything the first accepts, plus the optional
cardinality operators (`<?`, `?>`) that mean "zero or one" rather than "exactly one".

Reading with the old one was a silent ceiling: those operators are what dbdiagram.io writes today,
and — the reason this changed — **what `@dbml/core`'s own SQL importer emits**. A PostgreSQL schema
with a nullable foreign key converts to `Ref: a.id <? b.a_id`, which the classic grammar rejected
with `Expected " " but "?" found`. Without the swap, `DBML-020` would have handed the app a document
it could not read.
**Inputs / outputs**: unchanged — `parseDbmlModel` still answers `{ ok, model }` or `{ ok, error }`.
**Edge cases**: the two disagree in exactly one place found: which schema a **cross-schema `Ref`** is
filed under. `parseDbmlModel` concatenates every schema's refs, so it never sees the difference. Both
report failures as `CompilerError { diags }` carrying a `location.start`, which is what
`formatParseError` unpacks — the message text differs between them, the shape does not.
**Frontend dependency**: none outward.
**Markers**: none.

---

### DBML-020 SQL import is delegated, and SQLite is refused rather than approximated
**Implementation**: `renderer/src/lib/dbml/importers/sql.ts`
**Behaviour**: PostgreSQL, MySQL/MariaDB and SQL Server DDL become DBML through `@dbml/core` itself:
parse the dialect, export as `dbml`. Nothing is hand-written — a DDL parser per dialect is exactly
the code not worth owning.
**Inputs / outputs**: `(sql, "postgres" | "mysql" | "mssql")` → DBML text with a trailing newline.
**Edge cases**: **SQLite is absent on purpose.** The parser offers `mysql`, `postgres`, `mssql`,
`snowflake`, `oracle` and `schemarb`, and has no SQLite grammar; feeding SQLite DDL to a neighbouring
dialect half-works, which is worse than saying no — a SQLite database is read by introspection
instead. An empty script is refused, and so is one that parsed but yielded no tables, which is what a
near-miss dialect produces: replacing the user's document with an empty one would be the silent
version of that. A syntax error carries its `(line:column)` into *their* SQL.
**Frontend dependency**: `components/dbml/ImportDbmlModal.tsx`.
**Markers**: none.

---

### DBML-021 The Prisma importer is written here, and round-trips with the exporter
**Implementation**: `renderer/src/lib/dbml/importers/prisma.ts`
**Behaviour**: `@dbml/core` has no Prisma grammar in either direction, so this is the mirror of
`exporters/prisma.ts`: a brace scanner over `model` and `enum` blocks, the shape the rest of the repo
uses for formats it owns. It reads `@@map`/`@map` (the real table and column names), `@@schema`,
`@id`/`@unique`/`@@id`/`@@unique`/`@@index`, optionality, `@default`, the native `@db.…` type — which
wins over the Prisma scalar, because that is the column's real type — and `@relation`, from which it
writes the `Ref:` lines. It ignores what is Prisma's own bookkeeping and has no database counterpart:
`generator`, `datasource`, `@updatedAt`, and `view`/`type` blocks.

**The two files are built to round-trip**, and a test asserts it: every SQL type emitted here is one
the exporter's `mapType` maps back, so a schema that goes out and comes in again is the schema it
started as rather than drifting a little on each pass.
**Inputs / outputs**: `(source)` → DBML text.
**Edge cases**: a relation field is **not** a column — `posts Post[]` and `author User` describe the
relation, not storage. Only the side carrying `@relation(fields:)` writes the `Ref:`; the other side
is the same relation seen from the far end, and writing both would draw every line twice. An implicit
many-to-many has no such side on either model, so it is recognised by its shape — a list on both ends
— and attributed to whichever model sorts first, so it is written once. A foreign key that is itself
unique is a one-to-one (`-`), anything else many-to-one (`>`). `@default` is read by **counting
parentheses, not by regex**: `dbgenerated("gen_random_uuid()")` has two levels and a string, and a
`[^)]*` pattern stopped at the first `)` and dropped every function-shaped default. Comments are
stripped with string literals respected, so the `//` in `@default("https://x")` is not one.
**Frontend dependency**: `components/dbml/ImportDbmlModal.tsx`.
**Markers**: none. The round-trip test is what found `DBML-015`'s enum-casing bug: the exporter
matched an enum case-insensitively and then emitted the column under the key it was found by, so
`enum Estado` produced a field typed `estado` — a Prisma schema that does not compile.

---

### DBML-022 Importing creates a document, and never overwrites the open one
**Implementation**: `renderer/src/components/dbml/ImportDbmlModal.tsx` · `renderer/src/state/dbmlStore.ts`
**Behaviour**: An import names a **new** `.dbml` file and opens it. "Import" and "replace what I am
looking at" are different asks and only one of them is reversible; the document that was open stays
exactly as it was. The content is converted **before** the file is named on disk, so a script that
cannot be read says so instead of leaving an empty document behind.

`createDocument(rootPath, name, contents?)` carries it: the same path the "new schema" dialog takes,
with the starter example as its default. Naming is `documentPath.ts` either way (`DBML-003`).
**Inputs / outputs**: pasted text or a file picked through `apiPickFile`; the file name defaults to
the picked file's stem, and only while the field is untouched — a name the user typed outweighs one
derived for them.
**Edge cases**: the module's other three actions need an open document; this one does not, and its
button is never disabled.
**Frontend dependency**: none outward.
**Markers**: none.

---

### DBML-023 A connection is a thing on the machine, and reading is all it can do
**Implementation**: `src/CodeFlow.App/Dbml/DbmlConnectionStore.cs` · `Dbml/Introspectors/`
**Behaviour**: Four engines can be read: PostgreSQL, SQL Server, MySQL/MariaDB and SQLite. A saved
connection carries what is needed to reach one — driver, host, port, database, username, TLS, and
`file_path` for the engine that is a file rather than a server.

**Read-only, structurally.** No introspector issues DDL or DML; a schema designer that could write
to the database a person pointed it at is a different and much more dangerous tool. Connections are
scoped to neither a project nor a workspace: the same staging database is read from whichever folder
happens to be open.
**Inputs / outputs**: the five `dbml_*_connection` / `dbml_introspect_database` commands.
**Edge cases**: an unrecognised driver is an **error**, unlike the AI engine catalogue which falls
back to Claude — a schema read with the wrong engine is not a degraded answer, it is a wrong one.
Deleting removes the credential **before** the row: a row that outlives its secret asks for the
password again, while a secret that outlives its row is one nothing will ever read or clean up.
Saving writes the row **before** the credential, so a failed insert cannot file a secret under an id
that does not exist.
**Frontend dependency**: `components/dbml/DbConnectionPanel.tsx`.
**Markers**: none.

---

### DBML-024 The password is the one thing that never crosses the boundary
**Implementation**: `src/CodeFlow.App/Security/CredentialStore.cs` (`DbPasswordKey`) · `Dbml/DbmlCommands.cs`
**Behaviour**: `db_connections` has **no password column**. The secret goes to the OS credential
store under `db-password:{id}` and is read only inside the sidecar, to build a connection string that
never leaves the process. `DbmlConnection` — the record that crosses IPC in both directions — has no
field for it, so this is enforced by the type rather than by care.

Keyed by the connection's id rather than by host or database name: renaming a host must not strand
the password, and two logins to the same server are two secrets.
**Inputs / outputs**: `password` travels renderer → sidecar only, on `dbml_save_connection`.
**Edge cases**: **a blank password means "leave what is stored"**, not "clear it". The renderer
cannot show what is held, so an untouched field arrives empty on every edit, and treating that as a
clear would wipe the secret each time somebody fixed a typo in the port. A failure to reach the
server reports the **driver's own sentence only** — several drivers put the connection string in
their exception, which would otherwise carry the password into a toast, a log and a bug report.
**Frontend dependency**: `lib/dbml/connectionError.ts`, and the `DB_CONNECTION_REFUSED: ` sentinel
in `13-cross-language-contracts.md`.
**Markers**: `VERBATIM` on the key format and on the sentinel.

---

### DBML-025 The sidecar reports a schema; the renderer writes the document
**Implementation**: `src/CodeFlow.App/Dbml/DbmlSnapshotBuilder.cs` · `renderer/src/lib/dbml/emitDbml.ts`
**Behaviour**: `dbml_introspect_database` answers with a **structured snapshot**, not DBML text, and
`emitDbml` turns it into a document. So DBML emission lives in exactly one place — a pure function a
node test can call — instead of once per engine in C#, where nothing could test it without a server
and four copies would drift.

Between the two sits `DbmlSnapshotBuilder`, which is the other half of the same idea: every engine
answers the same five questions in its own dialect and *as rows*, so assembling those rows into a
schema is identical work done once. That is also what makes all four engines testable — a synthetic
row set proves the assembly without a PostgreSQL, a SQL Server and a MySQL to connect to.
**Inputs / outputs**: rows → `DbmlSchemaSnapshot` → DBML text.
**Edge cases**: a column is `pk` when a primary-key constraint names it, and `unique` only when a
**single-column** unique constraint does — a member of a two-column unique key is not unique on its
own. A single-column key produces **no index block**, because the column's own setting already says
it; a composite one does, because DBML has nowhere else to put it. The index backing a constraint is
reported by every engine alongside the constraint itself, and is dropped rather than drawn twice.
`NO ACTION` is dropped, since it is what every key that declares nothing reports. A relationship is
always written `>`: the snapshot carries a constraint, not a cardinality, and claiming one-to-one
from a foreign key alone would be a guess the database did not make.
**Frontend dependency**: `components/dbml/ImportDbmlModal.tsx`.
**Markers**: none.

---

### DBML-026 Each engine's catalogue, and what it takes to read it correctly
**Implementation**: `src/CodeFlow.App/Dbml/Introspectors/`
**Behaviour**: Four query sets behind one interface. **PostgreSQL** reads columns from
`information_schema` and everything else from `pg_catalog`, because that is the only place member
*order* survives — a composite foreign key read through `constraint_column_usage` comes back with its
columns unordered, which pairs the wrong ones together and is silent about it. It is also the only
engine with real enumerated types. **SQL Server** uses `INFORMATION_SCHEMA` for columns and `sys` for
keys, relations and indexes, for the same ordering reason (`sys.index_columns.key_ordinal`).
**MySQL** is the friendliest: `COLUMN_TYPE` already carries the arguments and `EXTRA` says
`auto_increment` outright; every query is scoped with `DATABASE()`, without which
`information_schema` returns every table on the server. **SQLite** reads `PRAGMA` functions, one
round trip per table, and is opened `ReadOnly` so a path that does not exist is refused rather than
created.
**Inputs / outputs**: a connection and its password → a `DbmlSchemaSnapshot`.
**Edge cases**: each engine reports defaults in its own wrapping and each is unwrapped — PostgreSQL's
`::cast`, SQL Server's doubled parentheses, SQLite's quotes; a PostgreSQL `nextval(…)` is dropped
because `increment` already says it. **SQLite's rowid alias requires a primary key of exactly one
INTEGER column**: read row by row, the first member of a composite key looked like one, and a join
table imported as `pedido_id INTEGER [pk, increment]` — found by importing a real database, fixed,
and pinned by a test. SQLite's `AUTOINCREMENT` is visible only in the stored DDL, which is why the
table's `sql` is read. Every catalogue query carries a 30-second timeout, for the server that accepts
the socket and then goes quiet.
**Frontend dependency**: none outward.
**Markers**: none.

## Test coverage

| Test | Source | Kind |
|---|---|---|
| `DbmlCommandsTests` (21) | `src/CodeFlow.App/Dbml/` | scenario — real temp directories and a real migrated database |
| `DbmlAssistantTests` (16) | `src/CodeFlow.App/Dbml/DbmlAssistant.cs` | seam — `ScriptedEngine` over the `AiRunner` delegate, no subprocess |
| `DbmlSnapshotBuilderTests` (17) | `src/CodeFlow.App/Dbml/DbmlSnapshotBuilder.cs` | pure — synthetic rows, so it covers all four engines' assembly |
| `SqliteIntrospectorTests` (12) | `src/CodeFlow.App/Dbml/Introspectors/SqliteIntrospector.cs` | scenario — a real SQLite file and real `PRAGMA` calls |
| `MigrationTests` (table and index counts) | `src/CodeFlow.App/Storage/Schema.cs` | scenario |
| `parse.test.ts` (9) | `renderer/src/lib/dbml/parse.ts` | boundary over `@dbml/core`, grammar included |
| `layout.test.ts` (14) | `renderer/src/lib/dbml/layout.ts` | pure — invariants, never pixels |
| `edges.test.ts` (2) | `renderer/src/lib/dbml/edges.ts` | pure |
| `routing.test.ts` (12) | `renderer/src/lib/dbml/routing.ts` | pure — shapes, lanes, markers |
| `inflect.test.ts` (56) | `renderer/src/lib/dbml/inflect.ts` | pure — case tables in both languages |
| `relationPhrase.test.ts` (8) | `renderer/src/lib/dbml/relationPhrase.ts` | pure — sentence choice and agreement |
| `exporters/sql.test.ts` (5) | `renderer/src/lib/dbml/exporters/sql.ts` | boundary over `@dbml/core` |
| `exporters/prisma.test.ts` (17) | `renderer/src/lib/dbml/exporters/prisma.ts` | pure — types, keys, both ends of every relation |
| `importers/sql.test.ts` (8) | `renderer/src/lib/dbml/importers/sql.ts` | boundary — every case re-parses the output |
| `importers/prisma.test.ts` (17) | `renderer/src/lib/dbml/importers/prisma.ts` | pure, plus the round trip against the exporter |
| `emitDbml.test.ts` (12) | `renderer/src/lib/dbml/emitDbml.ts` | pure — every case re-parses the output |
| `connectionError.test.ts` (4) | `renderer/src/lib/dbml/connectionError.ts` | pure — the sentinel |
| `assist.test.ts` (9) | `renderer/src/lib/dbml/assist.ts` | pure — the four proposal states and the table delta |
| `viewport.test.ts` (5) | `renderer/src/lib/dbml/viewport.ts` | pure |
| `documentPath.test.ts` (12) | `renderer/src/lib/dbml/documentPath.ts` | pure |
| `dbmlStore.test.ts` (18) | `renderer/src/state/dbmlStore.ts` | store, `lib/ipc/commands` mocked |

The view itself is not tested: the renderer's Vitest runs `environment: "node"` with no jsdom and no
testing-library, so component behaviour is covered by extracting its logic — which is why
`documentPath.ts` is a module and not a function inside the modal.

## Markers raised

None yet.
