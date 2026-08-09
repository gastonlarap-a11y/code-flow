# UX Redesign — audit, specification and phased implementation plan

> Written 2026-08-01 against v1.8.0. This document is the contract for the visual/UX overhaul:
> Part I is what exists (every claim carries a `file:line` you can grep), Part II is what we are
> building instead, Part III is the order we build it in. Nothing in here changes IPC contracts,
> persisted keys, or the sidecar — this is renderer-only.
>
> Direction chosen by the operator: **hybrid** — professional density (Linear/VS Code) in work
> zones (trees, diffs, editor, terminal), air and clarity (Notion) in configuration and action
> surfaces (Settings, modals, PR/AI panels). The operator's core complaint, verified
> quantitatively below: *controls are tiny and it is unclear what each one does*.

---

## Part I — Current state (audit)

### I.1 The control problem, in numbers

- **388 `<button>` elements** across ~100 component files, with **no shared Button primitive**.
  38 near-duplicate "primary button" className strings exist (all containing
  `bg-[var(--cf-accent)]`), drifting in padding (`px-2`/`px-2.5`/`px-3`), height and
  disabled-state opacity (`40` vs `50`).
- **Tooltips**: no Tooltip component exists anywhere. All labeling is the native `title`
  attribute (slow, unstyled, not keyboard/touch-accessible). 236 `title={t(...)}` uses are
  properly i18n'd; only 29 `aria-label`s exist in the whole app — 3 of them hardcoded English
  (`TitleBar.tsx:40,47,54`).
- **19 icon-only buttons have neither `title` nor `aria-label`** — 14 of them are the modal
  close `X` (e.g. `CloneRepoModal.tsx:103`, `ShortcutsModal.tsx:73`, `ConflictResolveModal.tsx:95`).
- **Icon sizes**: 444 of 570 lucide instances (78%) are ≤12px. Sizes above 16px are practically
  absent (16 instances app-wide).
- **Hit targets**: the most common icon-button box is 20px (`h-5 w-5` ×35); `h-4 w-4` (16px)
  exists (`GitSection.tsx:57`, `ProjectRow.tsx:163`). WCAG 2.2 SC 2.5.8 sets 24px as the floor.
- **Hover-only controls**: 32 `group-hover:*` reveals across 17 files — worst in
  `GitSection.tsx` (8) and `ProjectRow.tsx` (6). Row actions (rename/delete/merge branch, stash
  apply/pop/drop, move project) are invisible until the pointer rests on the exact row; nothing
  signals they exist.
- **22 files hand-roll a modal shell** (backdrop + panel + own escape/backdrop-click handling);
  only `ConfirmModal` centralizes it, and only for confirms.
- **Icon polysemy** (meaning depends on a tooltip half the buttons don't have):
  - `Square` = "stop process" (`DebugPanel.tsx:148`, `ChatPanel.tsx:131`, `RunnerModal.tsx:686`…)
    **and** "maximize window" (`TitleBar.tsx:51`).
  - `X` = close modal / cancel in-flight send (`RequestBuilder.tsx:838`) / remove item
    (`ProvidersSection.tsx:567`) / clear filter.
  - `Trash2` = two different severities on one screen (`SkillsSettings.tsx:215` delete skill vs
    `:321` delete file inside it).
  - `Sparkles` = fourteen unrelated AI actions with no secondary cue.

### I.2 The typography problem

There is no type scale. Text size is an arbitrary pixel value chosen per component:
`text-[11px]` ×308 · `text-[12px]` ×281 · `text-[13px]` ×148 · `text-[10px]` ×104 ·
`text-sm` ×23 · fractional oddities (`text-[10.5px]` ×7). **92% of sized text is ≤13px.**
Line height is picked independently (`leading-snug`/`-relaxed`/`-5`/`-none`/arbitrary), never
paired with size. Weights are the one consistent axis (`font-medium`/`font-semibold` dominate).

### I.3 What the visual system already does well (keep all of this)

- **Token discipline**: every UI gray routes through `--cf-*` variables (`index.css:9-27`);
  zero raw Tailwind gray families anywhere. 24 code themes drive both app chrome and Monaco
  from one palette (`lib/codeThemes.ts`); 8 curated accents (`state/accentStore.ts`) that code
  themes cannot override (`codeThemes.ts:680-686`).
- **The eyebrow label** (`text-[11px] font-semibold uppercase tracking-wide text-muted`) — used
  67× across 30+ files; the one genuine system-wide convention.
- **`ActivePill`** (`common/ActivePill.tsx`) — the shared framer-motion selection indicator,
  already reused by the view tabs and Settings nav.
- **`Select`** (`common/Select.tsx`, 283 lines) — the most mature primitive; keep as the model
  for how new primitives should feel.
- Solid theme-token borders (no translucent hairline soup), minimal shadows, `cf-orb-*` loading
  animation already respecting `prefers-reduced-motion`.

### I.4 The visual system gaps

- `@theme` declares exactly one token (`--font-sans`, `index.css:5-7`). No `--font-mono`
  (mono is `ui-monospace, monospace` literals), no type scale, no spacing/radius/overlay tokens.
- **Focus is nearly invisible**: `focus-visible` appears in 3 files (9 uses). Inputs get a
  border-color change only; buttons and rows mostly have no focus indicator at all.
- **Hovers snap**: most `hover:bg-*` states carry no transition class.
- Overlay opacities are unregulated: modal scrims range `bg-black/10`–`/40` per modal; hover
  overlays use nine different opacity steps (`0.03`–`0.4`).
- The dark palette is defined **three times** in `index.css` (`[data-theme="dark"]` :30-47,
  `@media (prefers-color-scheme: dark)` :50-67, plus `codeflow-dark` in `codeThemes.ts`).
- **Panel-chrome schism**: Editor+API share the `CARD` language (`api/panelChrome.ts` —
  rounded-xl + border + shadow, explicitly copied so those two views match), while
  Sidebar/AiPanel are flush asides and Settings invents its own; header bars are hand-set at
  `h-8`/`h-9`/`h-10` per area, and the icon-button hover treatment is copy-pasted per file.

### I.5 The navigation problems

- **Four views** (`uiStore.ts:6`: `graph | changes | editor | api`) but the API view is a
  second-class citizen: not in the TabBar (`TabBar.tsx:24-28` renders only three pills), tucked
  in `WorkspaceMenu` — a full animated dropdown built for N tools that contains one
  (`WorkspaceMenu.tsx:28-30`) — with no `Mod+4` shortcut, excluded from `Mod+Alt+←/→` cycling
  (`shortcuts.ts:55`).
- **The AI panel is a priority stack, not tabs** (`AiPanel.tsx:114-142`: link-PR > selected PR >
  Analyze > Chat). "Analyze changes" is reachable only from a small icon inside the Changes
  view's unstaged-header (`ChangesPanel.tsx:466-478`) — undiscoverable from the panel itself.
- **Four incompatible sub-navigation idioms**: TabBar pills, ApiSidebar's local tab strip
  (`ApiSidebar.tsx:224-228`), Editor's icon rail (`EditorView.tsx:519-549`), Settings' vertical
  list (`SettingsView.tsx:44-60`).
- **Settings has 7 scattered entry points** (StatusBar gear, `Mod+,`, palette, ShortcutsModal
  link, two `PullRequestsSection` deep-links, `ChatModelPicker`, `ChatPanel` nudge).
- **Back/forward is shallow** (`navigationStore.ts` replays `{view, projectId}` only) — Settings
  sections, API tabs, open files and AI panel state don't participate.
- The sidebar's project row is the *only* route to PR linking/connecting/creating
  (`PullRequestsSection.tsx`) even though PR review happens in the AI panel.

### I.6 Constraints any implementation must respect

From the renderer conventions in `AGENTS.md` and the code:
1. `FileTree`/`CollectionTree` are virtualized; row height is seeded (`estimateSize: () => 24`,
   `FileTree.tsx:491`, `CollectionTree.tsx:476`). Density changes touch both flatten libs and
   their tests; nothing may assume rows are mounted.
2. Drag-and-drop is pointer-driven (`lib/pointerDrag.ts`); never HTML5 `draggable`.
3. Monaco enters only via `lib/monacoEditor.ts`, always behind `lazy()`.
4. Every new label/tooltip lands in both locales of `lib/i18n/translations.ts`
   (parity is tested by `scripts/i18n-parity.test.mjs`).
5. Tests are node-env (no jsdom): primitives are tested through their pure logic (variant
   resolution, label requirements, keyboard state machines), not their render. **Decision**:
   jsdom stays out for now — the primitives below are deliberately thin over native elements,
   and CDP smoke passes cover the rendered behaviour. Revisit only if a primitive accumulates
   untestable branching.
6. Sentinel error prefixes and snake_case payloads are contracts; redesigned error UI keeps
   matching them by substring.

---

## Part II — Specification

### II.1 Operating principles (the hybrid, made concrete)

| Zone | Language | Surfaces |
|---|---|---|
| **Work** — dense, quiet, keyboard-first | 24px rows, 13px text, CARD chrome | FileTree, CollectionTree, diffs, Graph, Editor, Terminal, transcript lists |
| **Decide** — airy, labeled, self-explanatory | 15px body, visible text on buttons, generous padding | Settings, all modals, PR review actions, AI panel actions, empty states, onboarding |

**Non-negotiable minimums** (enforced by the primitives, not by review):
- Interactive hit target ≥ **24×24px** everywhere; ≥ **28×28px** in "decide" zones.
- UI text ≥ **12px**; 10–11px reserved for eyebrows and badges only.
- Every icon-only control has a tooltip **by construction** (the primitive's label prop is
  required and feeds both tooltip and `aria-label`).
- Visible `focus-visible` ring on everything interactive.
- Icons ≥ **14px** in toolbars, ≥ **16px** in rails; ≤12px only inside badges.
- Nothing conveyed by color alone; hover-revealed actions always have a persistent affordance.

### II.2 Design tokens (all in `index.css` `@theme`)

```css
@theme {
  --font-sans: "Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
  --font-mono: "JetBrains Mono", ui-monospace, "SF Mono", Menlo, monospace;   /* NEW */

  /* Type scale — size and line-height always travel together (NEW) */
  --text-badge: 11px;   --text-badge--line-height: 16px;  /* eyebrows, badges — floor */
  --text-ui: 12px;      --text-ui--line-height: 18px;     /* dense-zone default */
  --text-body: 13px;    --text-body--line-height: 20px;   /* lists, inputs */
  --text-relaxed: 15px; --text-relaxed--line-height: 22px;/* decide-zone body */
  --text-title: 18px;   --text-title--line-height: 26px;  /* modal/section titles */

  /* Radius scale (matches current de-facto tiers) */
  --radius-control: 6px;   /* buttons, inputs (today rounded-md) */
  --radius-card: 12px;     /* CARD panels (today rounded-xl) */

  /* Overlay scale — replaces the nine ad-hoc opacities */
  --overlay-hover: 0.05;   --overlay-active: 0.09;   --overlay-scrim: 0.35;
}
```

Plus, as `--cf-*` custom properties beside the existing ones:
- `--cf-focus-ring: 0 0 0 2px color-mix(in oklab, var(--cf-accent) 60%, transparent)` —
  applied via a shared `.cf-focusable` rule: `&:focus-visible { outline: none; box-shadow: var(--cf-focus-ring); }`.
- A base transition rule for interactive elements: `transition: background-color 150ms, border-color 150ms, color 150ms` (respecting `prefers-reduced-motion`).
- **Consolidate the dark palette to one definition**: keep `[data-theme="dark"]` as the single
  source; the `prefers-color-scheme` media block reduces to setting `data-theme` semantics only
  (themeStore already stamps the attribute on boot — `state/themeStore.ts`), deleting the
  duplicated variable block at `index.css:50-67`.

Migration note: text tokens are adopted **by area during Phase C**, not by a global
find-and-replace — each `text-[Npx]` is mapped to the nearest token when its area migrates, so
every visual change ships reviewed, in context.

### II.3 New primitives (`components/common/`)

Sketched APIs; all follow `Select.tsx`'s conventions (named export, i18n'd, theme tokens only).

```tsx
// Button.tsx — absorbs the 38 drifted variants.
type ButtonProps = {
  variant: "primary" | "secondary" | "ghost" | "danger";
  size?: "md" | "sm";              // md: h-8 text-body (decide zones); sm: h-7 text-ui (dense)
  icon?: LucideIcon;               // optional leading icon, size derived from `size`
  pending?: boolean;               // disables + swaps icon for spinner; prevents double-submit
  children: ReactNode;             // visible text — REQUIRED (icon-only goes to IconButton)
} & ButtonHTMLAttributes<HTMLButtonElement>;

// IconButton.tsx — the only sanctioned icon-only control.
type IconButtonProps = {
  label: TranslationKey;           // REQUIRED — feeds Tooltip AND aria-label; typed, so an
                                   // unlabeled icon button is a compile error, not a review nit
  icon: LucideIcon;                // rendered ≥14px
  size?: "md" | "sm";              // md: 28px box (decide); sm: 24px box (dense) — nothing smaller
  shortcut?: ShortcutCommandId;    // appends the live binding to the tooltip (generalizes
                                   // StatusBar's hint() pattern — StatusBar.tsx:28-56)
  variant?: "ghost" | "danger";
};

// Tooltip.tsx — replaces native `title` app-wide.
// Anchored, ~300ms delay, styled with theme tokens, shows optional shortcut chip.
// Renders nothing for touch/`prefers-reduced-motion` differences it can't honor — but the
// aria-label remains, so accessibility never depends on it.

// Modal.tsx — absorbs the 22 hand-rolled shells and the 14 unlabeled X buttons.
type ModalProps = {
  title: TranslationKey;           // becomes the h1 + aria-labelledby
  size?: "sm" | "md" | "lg";
  onClose: () => void;             // wired to: labeled X (IconButton), Escape, scrim click
  footer?: ReactNode;              // right-aligned Button row
};
// Focus moves in on open, is trapped, returns to trigger on close. Scrim uses --overlay-scrim.
// Built on native <dialog> for the trap/inert semantics.

// Tabs.tsx — one sub-navigation idiom for sections-within-a-panel.
// Generalizes ProviderTabs; uses ActivePill for the indicator; keyboard: ←/→ + Home/End.
// Adopters: ApiSidebar's Collections/Environments/History strip, EnvironmentModal's tab bar,
// RequestBuilder's panel tabs, AiPanel's new tabs (II.5).

// PanelHeader.tsx — one header bar: h-9, border-b, title slot (eyebrow style), actions slot
// (IconButtons). Absorbs the ad-hoc h-8/h-9/h-10 headers (AiPanel.tsx, ApiSidebar, Settings).

// RowActions.tsx — persistent "…" (MoreHorizontal) menu for row-level actions.
// Replaces the 32 group-hover reveals. The trigger is always visible at reduced opacity
// (not opacity-0), 24px hit box; menu items are icon+text, keyboard navigable.
// Adopters: GitSection branch/stash/remote rows, ProjectRow, FileTree/CollectionTree rows
// (careful: rendered inside virtualized rows — the menu itself portals to body, so row
// unmount on scroll closes it; that behaviour is correct and cheap).
```

**Icon dictionary** (one icon = one meaning; enforced by review + this table):

| Icon | Only meaning | Displaced usages |
|---|---|---|
| `Square` (filled) | stop a running process | TitleBar maximize → OS-native chrome glyphs (`Minus`/`Copy`-style or platform CSS) |
| `X` | dismiss/close a surface | "remove item" → `Trash2`; "cancel in-flight" → `Square`; "clear filter" → `XCircle` inside the input |
| `Trash2` | destructive delete (always `danger` variant) | — |
| `Eraser` | empty an ephemeral view — a stream transcript, the debug console | displaced `Trash2` at `stream/shared.tsx` (C2b) and `DebugPanel` (C4); nothing stored is lost |
| `Sparkles` | AI action **category** — never alone: always icon+text (`Button`), or `IconButton` whose tooltip names the specific action | 14 sites |
| `RotateCw` | refresh/re-check · `RotateCcw`: reset to default | codified, already mostly true |

### II.4 Panel chrome: resolving the schism

- **Work zones keep and extend `CARD`** (`api/panelChrome.ts` graduates to
  `components/common/panelChrome.ts`): Editor and API keep it; Graph and Changes adopt it so
  all four *views* read as one family.
- **Docked asides stay flush** (Sidebar, AiPanel, TerminalDock): full-height, single border,
  no radius — documented as the intentional second language for chrome that touches the window
  edge. Both asides adopt `PanelHeader`.
  **Superseded by Phase 3 of `docs/REDESIGN-PROPOSAL.md` (§4.6).** The second language is gone:
  nothing touches the window edge any more, so every panel wears `CARD` and floats over the ambient
  background. `PanelHeader` stands. See `components/common/panelChrome.ts` for the current rule.
- **Settings moves to the "decide" language**: `text-relaxed` body, `md` buttons with visible
  text, airier section spacing — it is the app's most label-hungry surface and today it is as
  cramped as the trees.

### II.5 Navigation redesign

1. **API becomes a first-class view**: fourth pill in the TabBar (`TabBar.tsx:24-28` +
   `WORKSPACE_VIEWS` gating stays — the pill renders regardless of project, like today's menu),
   `Mod+4` in `shortcuts.ts`, included in `VIEW_ORDER` cycling (`shortcuts.ts:55`).
   **`WorkspaceMenu.tsx` is deleted** — it is an N-tool menu shipping with one tool; if a second
   workspace tool ever exists, the TabBar grows a pill or the menu returns *with content*.
2. **AiPanel becomes tabbed** (`Tabs`): **Chat · PR review · Analyze**. The priority stack
   (`AiPanel.tsx:114-142`) becomes default-tab selection instead of exclusive rendering: a
   selected PR opens the PR tab, an analyze run opens Analyze — but the user can always switch.
   "Analyze changes" gains a launcher inside the Analyze tab (works on the current project's
   changes); the existing Changes-view entry (`ChangesPanel.tsx:466-478`) stays as a shortcut
   but is promoted from bare icon to `IconButton`.
3. **Settings entries**: canonical affordances are the StatusBar gear, `Mod+,` and the palette.
   The other five become documented deep-links (they already pass a section — keep them; they
   are good UX), all routed through the one `openSettings()` they already use.
4. **Sub-navigation, one idiom per level**: view switching = TabBar pills (+`ActivePill`);
   sections within a panel = `Tabs`; the Editor rail stays as the documented exception
   (VS-Code-familiar), upgraded to `IconButton`s at 16px icons.
5. **Row actions**: `RowActions` in GitSection, ProjectRow, FileTree, CollectionTree; the two
   or three highest-frequency actions may remain as inline `IconButton`s (visible, not
   hover-gated) — e.g. stash apply, branch checkout.
6. **Back/forward**: keep `{view, projectId}` scope but *say so* — TitleBar chevron tooltips
   become "Previous view". Deep history (files, sections) is explicitly out of scope for this
   redesign (its cost is architectural, its benefit unproven here).

### II.6 Per-area redlines

Each row lists the concrete changes; primitives referenced from II.3. (`file:line` = current state.)

| # | Area | Redlines |
|---|---|---|
| 1 | `layout/sidebar/GitSection.tsx` (8 hover-reveals, 16px hit boxes at `:57`) | Rows get `RowActions` (rename/drop/view for stashes; merge/delete/detach for branches) + one inline `IconButton` for the primary action (apply stash / checkout). Icons 14px in 24px boxes. Section headers keep eyebrow style. |
| 2 | `layout/sidebar/ProjectRow.tsx` (6 reveals, `h-4 w-4` at `:163`) | Reveal-in-Finder / open-in-VS-Code / move-to-workspace fold into `RowActions`; expand chevron and new-branch become `IconButton sm`. |
| 3 | `layout/sidebar/PullRequestsSection.tsx` (`:261-280` icon-as-hit-target) | create/open/refresh become `IconButton sm`; connect flows get `Button secondary` with text (decide zone); state banners keep deep-links. |
| 4 | `git/ChangesPanel.tsx` (20px toolbar at `:466-478`) | Toolbar becomes `IconButton sm` row under `PanelHeader`; commit button becomes `Button primary` with text; AI-generate becomes `Sparkles`+text per dictionary. |
| 5 | `layout/TitleBar.tsx` (`:40-54`) | Window controls: i18n'd aria-labels; maximize loses `Square` (dictionary). Back/forward/AI-menu → `IconButton` with shortcut tooltips. |
| 6 | `settings/*` (all sections) | Decide-zone typography (`text-relaxed` body), `Button md` with text everywhere (no bare icons for destructive acts — `Trash2` + text), inputs with visible labels, focus rings. `ProvidersSection`/`SkillsSettings` get the same treatment as the worst offenders. |
| 7 | `api/RequestBuilder.tsx` (split-button `:843-868`) | Send cluster: `Button primary` (Send) + separated `IconButton` (send-and-download) with distinct tooltip; cancel-in-flight uses `Square` per dictionary (today `X` at `:838`). Panel tabs → `Tabs`. |
| 8 | `api/EnvironmentModal.tsx` + the other 5 API modals | Migrate to `Modal`; `p-1` icon buttons → `IconButton sm`; reveal/hide secret gains text state ("Visible") next to the eye icon. |
| 9 | `ai/AiPanel.tsx` + sections | `Tabs` (II.5), `PanelHeader`, action buttons → `Button`/`IconButton`; PrReviewPanel's disabled-state explanations move from `title` (`PrReviewPanel.tsx:180,363,550,564`) to `Tooltip` on a wrapper (tooltips on disabled controls work — the primitive anchors on a span). |
| 10 | `editor/EditorView.tsx` rail (`:519-549`) + `DebugPanel.tsx` toolbar | Rail icons 16px in 28px boxes with tooltips+shortcuts; Debug step icons get distinct glyphs and tooltips; both keep dense-zone sizing. |
| 11 | All 22 modal shells (list in audit) | Migrate to `Modal` — titles become real headings, every X labeled, focus trapped, scrims unified. |
| 12 | `layout/StatusBar.tsx` | Already the best-behaved toolbar (`hint()` pattern, `h-6 w-6`); upgrade boxes to 24px, keep as the reference example. |

### II.7 Accessibility checklist (leaves this redesign done, not aspirational)

Closed in phase D, each against evidence rather than an assertion.

- [x] **Every interactive element: visible `focus-visible` ring.** `index.css:157-160` defines
  `.cf-focusable`; `lib/ui/controlStyles.ts`'s `BASE` puts it on every `Button` and `IconButton`, so
  the ring is not something a call site can forget. The rows that are not primitives — picker
  results, workspace entries, the reorder handle — carry it explicitly.
- [x] **Every icon-only control: tooltip + `aria-label`.** Structural, not audited: `IconButton`'s
  `label` is a required `TranslationKey` feeding both, so an unlabelled icon button does not compile.
  The audit's starting point was 19 controls a screen reader called "button".
- [x] **TitleBar window controls: i18n'd `aria-label`s** — `TitleBar.tsx:58-81`. These are also the
  one place a native `title` survives, and `ui-conventions.test.mjs` allows exactly that file.
- [x] **Modals: focus trap, labelled titles, Escape, focus restored.** `lib/useDialog.ts` +
  `common/Modal.tsx`, and `PickerModal` for the three search-first dialogs that have no heading
  because the field is the header.
  **Focus restoration was broken in every dialog with an autofocused field**, which is most of them,
  and only the live check found it: React applies `autoFocus` while committing the panel, before any
  effect runs, so `useFocusTrap` captured the dialog's own input as "what to go back to" and on close
  handed focus to a node it had just unmounted. A CDP trace of `focus()` showed
  `focus() -> Buscar ramas… [DETACHED]` and then `<body>`. It restores from a two-entry focus history
  now, which also gets nesting right — closing a dialog opened from inside another returns to the
  parent's control rather than to whatever opened the parent.
  **Native `<dialog>` was decided against, not deferred.** `showModal()` promotes the dialog into
  the top layer, above which the `createPortal` menus of `Select`, `ColorSwatchPicker`,
  `ChatModelPicker`, `CodeSnippetPanel` and `VariableInput` cannot render — and `Select` alone
  appears inside five modals. The gain over the current hooks is `inert` and top-layer stacking; the
  cost is rewriting the app's most-used primitive. Revisit only if those menus move to the popover
  API for their own reasons.
- [x] **Text contrast ≥4.5:1 in both themes.** `--cf-text-muted` passes on every surface: 5.22:1 on
  light `--cf-surface`, 4.88:1 on light `--cf-bg`, 5.77 / 5.21 / 6.28:1 dark.
  The pass also found what nobody had measured: **white on `--cf-accent` failed on six of the eight
  accent options in the light theme and on all eight in the dark**, 4.47:1 at best and 1.67:1 at
  worst. `--cf-accent-solid` / `--cf-accent-on-solid` split the accent's two jobs — ink and fill —
  and `state/accentStore.test.ts` holds every option to the floor. `ConfirmModal`'s filled
  destructive confirm (2.77:1 dark) became the system's `danger` variant.
  The status colours had the same shape of problem against a second variable: they are declared once
  while `codeThemes.applyThemeVars()` repaints the surface under them from any of 21 themes. Chosen
  against white they passed; against `tokyo-night-light`'s #e1e2e7 they measured 3.73–3.88:1. All
  three light shades dropped two steps, which clears every light theme with margin, and
  `scripts/theme-contrast.test.mjs` now checks each token against every theme's actual surface.
- [x] **All animation behind `prefers-reduced-motion`** — `index.css:170-174` for `.cf-interactive`,
  `:344` for the rest; `UpdateAlert` reads `useReducedMotion()` before it animates.
- [x] **Nothing colour-only.** PR state badges carry an icon and a label beside the tone
  (`PrReviewPanel.tsx` `PR_STATE_TONES`); toasts gained `role="alert"` / `role="status"`, so an error
  is announced rather than only tinted.

**Open, and deliberately so** — one colour decision, a visual judgement rather than a correction:

1. **The accent as ink.** Accent-coloured text on a surface measures 2.43–4.53:1 on six of the eight
   options in the light theme (all eight pass in dark). Raising it means changing the colour the user
   picked from the palette, not a token behind it. The 2.0 chroma pass held lightness precisely so
   this would not get worse: the worst case, `cyan`, measures 2.43:1 before and after.

**Closed since**:

2. ~~**`--cf-danger` in the dark theme**~~, on the four themes whose "raised" surface is unusually
   light for a dark theme — `darcula` (#4e5254) at 2.86:1, then `nord`, `gruvbox-dark`, `dracula`.
   Settled by the 2.0 palette: neither neighbouring shade on Tailwind's scale worked (red-300 still
   missed `darcula` at 4.16:1, red-200 cleared at 5.46:1 but had turned pink), so the token is now a
   value between them, off the scale, clearing all eleven dark themes at 4.58:1 on the worst. The
   exemption list in `scripts/theme-contrast.test.mjs` is gone.

---

## Part III — Phased implementation

Each phase is one mergeable PR; the app stays fully functional after every merge. Verification
for every phase: `pnpm -C renderer typecheck && pnpm -C renderer test && pnpm -C renderer lint`,
plus a CDP visual smoke of the touched surfaces (screenshots, before/after, shared with the
operator). No CI: `ci-web` is switched off, so those three commands are the whole gate.

- **Phase A — tokens + primitives** (no consumer migrations).
  `@theme` tokens, focus/transition rules, dark-palette consolidation; `Button`, `IconButton`,
  `Tooltip`, `Modal`, `Tabs`, `PanelHeader`, `RowActions` with pure-logic tests (variant/size
  resolution, required-label typing, menu keyboard state machine) and a temporary showcase
  harness for CDP screenshots. **Decision gate with the operator**: tree row density (keep 24px
  vs 26px — touches both flatten libs) reviewed on before/after screenshots here.
- **Phase B — navigation + modal shells.** *(Done. Three things landed differently from the
  specification above, each for a reason found in the code — recorded here so II.5 is not read as
  the final word:)*
  - `WorkspaceMenu` is deleted, but its **workspace indicator moved to the StatusBar** rather than
    disappearing. Its trigger was the only place the active workspace was named, and
    `Sidebar.tsx` returns `null` when collapsed, so `Mod+B` left nowhere to read it. The status bar
    already shows project › branch and never collapses.
  - The API pill sits in **its own group behind a divider**, not appended to the three repo tabs.
    The `ScopeMarker` in front of them claims they follow the selected repository, and the API view
    does not — the divider keeps that claim true.
  - The AI panel's tabs use **manual activation**: its Analyze tab starts a Claude run on mount, so
    selection-following-focus would spend money on an arrow key. `Tabs` grew an `activation` prop
    and the `tabPanelProps` half of the ARIA pattern it was missing.
  - The modals that stayed hand-rolled are the ones a generic shell would damage: `ConfirmModal`
    (no heading by design), `SecretScanModal` (two exits), `ConflictResolveModal` (near-fullscreen
    Monaco), `StashDiffModal` (title is data), `SettingsView` (full screen, no scrim dismiss), the
    three pickers (`CommandPalette`, `FilePalette`, `BranchSwitcherModal`), `ActivityModal` (second
    chrome row) and `OpenPrLinkModal` (five states, nests another modal).
- **Phase C — area migrations.** Split into **five** PRs rather than the three or four sketched
  here, because measuring the areas showed two of them are far larger than the rest: `api/` is 29
  files and ~12,400 lines, `settings/` 24 files with 68 buttons and *two* `aria-label`s in the
  whole directory. The order is C1 layout+git → **C2a** API shell → **C2b** API panels and modals →
  **C3a** Settings → **C3b** AI panel sections → **C4** Editor+Debug+Graph. Each PR maps its area's
  `text-[Npx]` to tokens, replaces buttons with primitives, applies the redlines (II.6), and deletes
  the local copies the primitives absorb.
  - **C1's own typography was carried into C2a as "C1.1".** C1 migrated its controls but left 38
    `text-[Npx]` behind in the six files it touched. Deferring that to Phase D would have turned D
    into exactly the global find-and-replace §II.2 rules out.
  - **`scripts/ui-conventions.test.mjs` makes the migration enforceable.** It fails on a
    `text-[Npx]` or a `<button title=…>` in any file not on its exemption list, and every phase-C PR
    deletes its own entries. Without it, "migrated by area" is a promise; with it, it is CI.
  - **`StopSquare` (`lib/ui/icons.ts`) is now the only stop glyph.** The dictionary's entry is the
    *filled* square, and the fill was being hand-written in four files with one omission — so the
    finished glyph is exported instead of the rule.
  - **The send button's caret is gone** (C2a). It opened a menu containing exactly one item, which
    is the same shape `WorkspaceMenu` was deleted for; send-and-download is its own `IconButton`.
  - **`panelChrome.ts` graduated to `components/common/`** in C2a: `CARD` was being re-typed by hand
    five times outside `api/`.
  - **C2b finished `api/`**: the 23 files it names are exactly the `api/…` entries the guard was
    exempting, so the directory now holds no `text-[Npx]` and no native `title`. It also deleted
    `ApiModal`'s `@deprecated` `PrimaryButton`/`GhostButton` and their 43 callers, and folded three
    identical eye-toggles into `api/RevealToggle.tsx` plus two identical eyebrow labels into
    `api/LabeledField.tsx`.
  - **The reveal toggle names the action, not the state.** §II.6 row 8 asked for "Visible" beside the
    eye; it says "Show value"/"Hide value" instead, because that string is the button's accessible
    name and a button's name has to describe what pressing it does.
  - **`CollectionTree`'s "…" did not become `RowActions`.** It opens the same menu the row's
    right-click opens, and building a second one for the button alone is how the two drift apart. The
    button became a permanent 24px `IconButton`, and `ContextMenu` grew arrow/Home/End/Enter handling
    from `lib/ui/menuNavigation.ts` — which fixes the right-click path and `ApiSidebar`'s overflow
    menu at the same time.
  - **`BodyPanel`'s mode selector stays a radiogroup.** Real `<input type="radio">`s already give
    arrow navigation and the right semantics; turning them into `Tabs` would trade that for
    consistency alone. Only the hit target and the type scale changed.
  - **Deferred, and named here so it is not mistaken for an oversight**: `RunnerModal`'s drag handle
    (`:727`) is pointer-only, so a collection's run order cannot be reordered by keyboard. That is a
    behaviour to add, not a control to migrate — it belongs to the II.7 pass.
  - **C3a emptied `settings/`.** 206 `text-[Npx]` plus 21 `text-sm`, 68 raw buttons and *two*
    `aria-label`s in the whole directory. Five local stand-ins for the primitives went with it:
    `btnPrimary`/`btnOutline` (and the same class string re-typed without the constant in two more
    files), `HEADER_BUTTON`, `RunAction` — an `IconButton` written by hand, down to the 24px box —
    `MarkButton`, and `inputClass` with its three literal copies.
  - **The guard now catches `text-sm` too.** It only ever looked for `text-[Npx]`, so a component
    could still pick its own size through Tailwind's own scale — and 21 of the app's 25 uses were in
    Settings, i.e. the loophole was load-bearing. The four outside it were fixed in the same change.
  - **`settings/Field.tsx` is where a label reaches its control.** Settings had **zero** `htmlFor`
    against 23 inputs; four labels sat as siblings of their input and named nothing, and about twenty
    inputs had only a placeholder, which stops being a name the moment you type. `Field` takes a
    function child so the id can reach the control without `cloneElement` guessing which descendant
    is the input.
  - **Destructive controls split by weight, not by rule.** §II.6 row 6 asks for `Trash2` + text; a
    row among twenty rows gets a labelled 24px `IconButton` instead, and the screen's own destructive
    action (purge every saved review, reset all app data, disconnect the account) carries text. The
    unnamed ones — four `Trash2` and one `X` — had no accessible name at all.
  - **`ThemeSettings`' `Panel` was *not* folded into `GroupCard`,** contrary to the phase plan.
    `GroupCard` is the top-level card (icon chip, `p-4`, subtitle) and the theme picker nests `Panel`
    three deep, where that padding compounds into a wall — and `Panel` carries a right-aligned
    summary so a folded row still shows what is selected. Two disclosure shapes, at two depths.
  - **`ColorSwatchPicker` was fixed here rather than in Phase D**: the CDP sweep found it was the
    last sub-24px control in Settings and its only label was a hex string. The dot stays 14px; the
    button around it is now 24 and says the colour's name.
  - **C3b emptied `ai/`** — 88 pixel sizes, 37 raw buttons, *one* `aria-label` in thirteen files.
    `PrReviewPanel` alone held ten native `title`s, the ones that explain why a control is
    unavailable; they are `Button`'s `tooltip` now, which anchors on a wrapping span because a
    disabled button fires no pointer events of its own.
  - **`ControlVariant` grew `success` and `warning`.** The three PR decision buttons carry their
    outcome in colour, and `PrActionButton` was spelling that out in a local `PR_ACTION_TONES` map
    with a comment about Tailwind never generating an interpolated `--cf-${tone}`. The variants live
    with the others now, and `controlStyles.test.ts` covers them because it enumerates the union.
    `PrActionButton` survives as a thin `Button` wrapper: what it adds is the layout, including the
    container query that drops the three labels below ~300px.
  - `ActivityModal` and `ChatModelPicker` kept their shells and their portal menu, as Phase B and the
    `Modal` doc-comment already decided; their *contents* migrated.
  - `ReviewSettings` and `SddSettings` became `Tabs` (their `TABS` arrays were already `TabOption`
    shaped). `SettingsView`'s nav did not: twelve sections in two labelled groups, vertical — a flat
    horizontal strip cannot express that, and a list of buttons that swaps a pane is a correct
    pattern on its own. Theme mode, tree density and language are choices rather than tabs, so they
    stayed buttons and gained the `aria-pressed` none of them reported.
  - **C4 closed phase C.** Editor, Debug, Graph, the four git modals and the terminal dock — and the
    git modals were included even though the phase plan named only Graph, because leaving them would
    have sent phase D back into an area phase C had otherwise finished.
  - **The editor rail reads its chords instead of carrying them.** Its tooltips said
    `" (Ctrl+Shift+F)"` as a hardcoded suffix, which a rebind — or an edit to `EDITOR_RESERVED` —
    would have left lying with nothing failing. `lib/shortcuts.ts` gained `reservedChordFor`, the
    reverse of the `reservedBy` that already existed, with a test asserting the two are inverses;
    `IconButton` gained `shortcutLabel` for chords Monaco owns and that therefore have no
    `ShortcutId`. `EditorPane`'s snapshot and split buttons read theirs the same way.
  - **`Toggle` in `CodeSnapModal` stayed local.** Two of its three uses render something that is not
    a lucide icon at all — three dots for the window controls, a letter for the path — and
    `IconButton` renders exactly one glyph by contract.
  - `EditorTabs` and `TerminalDock` were the app's last hover-only reveals. Neither became `Tabs`:
    they are document tabs, closable and dirty-able, not the single-panel ARIA switch. The unsaved
    dot and the close button are two marks now, both always there, as in `RequestTabs`.
  - **What phase D inherits is exactly the primitives and the layout modals** — `common/` (6 files)
    and `layout/` (11) — because every feature directory is off the guard's lists.
- **Phase D — polish + sweep. Done; this closes the redesign.**
  - **The guard has no exemption list.** `PIXEL_TEXT_PENDING` is deleted: the type-scale rule holds
    across every component with no exceptions, so a new `text-[13px]` or `text-sm` anywhere fails CI.
    `NATIVE_TITLE_PENDING` became `NATIVE_TITLE_ALLOWED` with one entry — `TitleBar`, which is the
    OS's own chrome keeping the OS's own tooltip, a reason rather than a backlog.
  - **`PickerModal` is new, and replaced a plan that could not work.** Phase D was to make
    `BranchSwitcherModal` a `Modal`; it cannot be one. `Modal` opens with an `<h2>` from its title,
    and the branch switcher has no heading — the search field *is* the header and the dialog's name
    is what you are searching for. The command palette and "go to file" are the same shape, and the
    three copies had drifted into three widths, two top offsets and two surface tokens.
  - **`Sidebar`'s three header buttons were the app's last sub-24px controls** — 20px boxes named
    only by a native `title`, which is a tooltip and not an accessible name.
  - **The accent was doing two jobs with one token.** See §II.7: white on `--cf-accent` failed AA on
    six of eight options in light and all eight in dark. `--cf-accent-solid` /
    `--cf-accent-on-solid` separate the fill from the ink, and a test holds the palette to the floor.
  - **`RunnerModal` can be reordered without a mouse.** Its handle was a `<span>` with
    `onPointerDown`; the reorder logic moved to `lib/ui/reorder.ts` — a pure function in a `.tsx` is
    untestable by construction here — where `moveBy` is defined in terms of the drag's `moveTo` so
    the two inputs cannot disagree.
  - `ChangesPanel` imports `CARD` instead of re-typing it, closing §II.4. `PrReviewPanel`'s
    hand-filled `Square` became `StopSquare`, closing the icon dictionary. `showcase.html`,
    `src/showcase.tsx` and `components/dev/` are deleted — the gallery existed to review the
    primitives while they were being built.
  - Transition defaults and `prefers-reduced-motion` were already landed in phase A
    (`index.css:148-174`), and `api/panelChrome.ts` had already graduated to `common/`; neither
    needed phase D.

**Traceability**: every audit finding in Part I maps to a remedy — controls→II.3+C,
typography→II.2+C, focus/transitions→II.2+A, chrome schism→II.4+D, navigation→II.5+B,
icon polysemy→II.3 dictionary+D, hover-only→RowActions+C, a11y→II.7+all.

**Open questions for the operator** — all three answered in phase A, on real screenshots:
1. **Tree row density**: neither. It became a setting (`state/densityStore.ts`, `lib/ui/density.ts`),
   because the answer differs per user and per screen.
2. **Mono font**: bundled — `@fontsource-variable/jetbrains-mono`, imported in `main.tsx`.
3. **Inter**: bundled the same way, so rendering no longer depends on what the OS happens to have.
