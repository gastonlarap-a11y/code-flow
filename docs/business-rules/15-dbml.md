# 15 — Schema designer

## Scope

- `src/CodeFlow.App/Dbml/` — `DbmlCommands.cs`, `DbmlDocuments.cs`, `DbmlLayoutStore.cs`,
  `DbmlAssistant.cs`, `DbmlTablePosition.cs`, `DbmlJsonContext.cs`
- `src/CodeFlow.App/Ai/Prompts/` — `DBML_EDIT_PROMPT.txt`, `DBML_REVIEW_PROMPT.txt`,
  `DBML_EXPLAIN_PROMPT.txt`
- `renderer/src/lib/dbml/` — `parse.ts` (the `@dbml/core` boundary), `model.ts`, `layout.ts`,
  `edges.ts`, `routing.ts`, `inflect.ts`, `relationPhrase.ts`, `viewport.ts`, `documentPath.ts`,
  `assist.ts`
- `renderer/src/lib/dbml/exporters/` — `sql.ts`, `prisma.ts`
- `renderer/src/state/dbmlStore.ts`
- `renderer/src/components/dbml/` — `DbmlView.tsx`, `DbmlCanvas.tsx`, `DbmlViewportControls.tsx`,
  `NewDbmlModal.tsx`, `ExportDbmlModal.tsx`, `DbmlAiModal.tsx`
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

## Test coverage

| Test | Source | Kind |
|---|---|---|
| `DbmlCommandsTests` (21) | `src/CodeFlow.App/Dbml/` | scenario — real temp directories and a real migrated database |
| `DbmlAssistantTests` (16) | `src/CodeFlow.App/Dbml/DbmlAssistant.cs` | seam — `ScriptedEngine` over the `AiRunner` delegate, no subprocess |
| `MigrationTests` (table and index counts) | `src/CodeFlow.App/Storage/Schema.cs` | scenario |
| `parse.test.ts` (8) | `renderer/src/lib/dbml/parse.ts` | boundary over `@dbml/core` |
| `layout.test.ts` (14) | `renderer/src/lib/dbml/layout.ts` | pure — invariants, never pixels |
| `edges.test.ts` (2) | `renderer/src/lib/dbml/edges.ts` | pure |
| `routing.test.ts` (12) | `renderer/src/lib/dbml/routing.ts` | pure — shapes, lanes, markers |
| `inflect.test.ts` (56) | `renderer/src/lib/dbml/inflect.ts` | pure — case tables in both languages |
| `relationPhrase.test.ts` (8) | `renderer/src/lib/dbml/relationPhrase.ts` | pure — sentence choice and agreement |
| `exporters/sql.test.ts` (5) | `renderer/src/lib/dbml/exporters/sql.ts` | boundary over `@dbml/core` |
| `exporters/prisma.test.ts` (16) | `renderer/src/lib/dbml/exporters/prisma.ts` | pure — types, keys, both ends of every relation |
| `assist.test.ts` (9) | `renderer/src/lib/dbml/assist.ts` | pure — the four proposal states and the table delta |
| `viewport.test.ts` (5) | `renderer/src/lib/dbml/viewport.ts` | pure |
| `documentPath.test.ts` (12) | `renderer/src/lib/dbml/documentPath.ts` | pure |
| `dbmlStore.test.ts` (18) | `renderer/src/state/dbmlStore.ts` | store, `lib/ipc/commands` mocked |

The view itself is not tested: the renderer's Vitest runs `environment: "node"` with no jsdom and no
testing-library, so component behaviour is covered by extracting its logic — which is why
`documentPath.ts` is a module and not a function inside the modal.

## Markers raised

None yet.
