# 14 — Work items (tickets)

Linking a branch to the ticket it is work for, caching that ticket, and writing a readable copy of
it to disk so both the developer and the AI read the same thing.

## Scope

- `src/CodeFlow.App/Tickets/` — the whole feature: paths, branch heuristic, HTML conversion,
  criteria extraction, account resolution, the store, the mirror, the sync, the review, the verdict
  parser and the commands
- `src/CodeFlow.App/Providers/WorkItemLink.cs` — parsing a pasted work-item address
- `src/CodeFlow.App/Providers/Azure/AzureWorkItem*.cs` — the Boards REST client, owned by
  `06-providers.md`
- `renderer/src/types/domain.ts` — the wire types (`Ticket`, `TicketWithLinks`, `TicketLink`,
  `TicketSummary`, `TicketAccount`, `TicketCriteria`, `TicketSuggestion`, `TicketLinkRef`,
  `TicketReviewResult`)

- `renderer/src/lib/parseTicketVerdict.ts` and `renderer/src/components/ai/TicketReviewSection.tsx`

The three tables (`tickets`, `ticket_links`, `ticket_review_runs`) are owned by `03-storage.md`. The
two verdict headers and the two refusal prefixes are `XLANG-016` and `XLANG-017` in
`13-cross-language-contracts.md`; the finding format they sit beside is `XLANG-001` and belongs to
`07-review-pipeline.md`.

## Read-only, deliberately

Nothing in this feature writes to Azure DevOps. Commenting and state transitions are specified but
not built, at the user's explicit instruction, so a defect here cannot alter anybody's board.
`TicketCommandsTests` asserts the absence of the write verbs rather than trusting it. The review it
runs is local: it reads the work item, writes its own row, and posts nothing.

## What the field survey found

Measured against a live organisation on 2026-08-10, over three Product Backlog Items of one active
sprint. It is recorded here because the design only makes sense against it:

| Source | Content | Conclusion |
|---|---|---|
| `Microsoft.VSTS.Common.AcceptanceCriteria` | 2 characters (`<div><b>-</b></div>`) on all three | Declared by 8 of 33 work item types, filled on none |
| The 16 `Custom.*` fields | Byte-identical across all three tickets | The refinement form, not its answers |
| `System.Description` | 3399 · 3773 · 886 characters, all different | The only real source of requirements |

`Technical Story` and `Task` do not declare the acceptance-criteria field at all.

## Rules

### WI-001 Ticket identity
**Implementation**: `TicketStore.IdFor`
**Behaviour**: a ticket is keyed `{provider}:{org}:{project}:{external_id}`, composed in the store
rather than by callers so the primary key and `idx_tickets_identity` cannot disagree about what
"the same ticket" means.
**Inputs / outputs**: four strings → one id.
**Edge cases**: `external_id` is text, not a number — Azure numbers work items and Jira names them.
**Frontend dependency**: `Ticket.id`, used as the argument to every by-id command.

### WI-002 Where a ticket's files live
**Implementation**: `TicketPaths`, `AppPaths.TicketsRoot`
**Behaviour**: `{root}/{org}/{project}/{id}-{slug}`, where root is the `tickets_root_dir` setting
and falls back to `{BaseDirectory}/tickets`. Blank counts as unset. The id leads the directory name
so directories sort and complete by the number a person quotes, and a retitled ticket keeps its
prefix.
**Edge cases**: `Slug` folds accented letters through an explicit table, **not**
`Normalize(FormD)` — the project builds with `InvariantGlobalization`, under which normalisation is
a no-op and *Facturación* would name its directory `facturaci-n`. Segments are cut at 60 characters
with no trailing separator.
**Case is preserved.** It was lower-cased first, and a user who opened the folder for *CF-E2E Ajuste
de tabla (criterios en prosa)* found `3-cf-e2e-ajuste-de-tabla-criterios-en-prosa` and reported it as
wrong. It was not wrong, but nothing was gained by it: what makes a path awkward is spaces and
punctuation, not capitals, and both are still gone. The fold table now spells its ASCII replacement
in the case it found, rather than calling `ToUpperInvariant` on the result — the same bet on
invariant mode in a different place.
**Frontend dependency**: `Ticket.mirror_path`.

### WI-022 A mirror never moves
**Implementation**: `TicketPaths.MirrorFor`, `TicketSync.RunAsync`
**Behaviour**: a ticket already mirrored keeps the directory recorded in its `mirror_path`; a fresh
name is computed only the first time it is seen. Blank counts as never mirrored.
**Edge cases**: the directory used to be recomputed from the current title on **every** sync, so
renaming the work item on the board silently relocated the mirror — and left the user's `notes/`, the
one thing in there nobody else owns, stranded in a directory the app would never open again. Nothing
moved them and nothing said so, which makes it the exact failure `WI-003` exists to prevent, reached
by a route `WI-003` did not cover. It is a named function rather than a condition inside the sync so
the rule can be stated once and tested without a network or a keychain — `TicketSync.RunAsync` needs
both, so the decision would otherwise have been untestable.
It is also what makes the case change in `WI-002` safe: existing directories are never recomputed, so
nothing already on disk is renamed.

### WI-003 The mirror owns four names and no more
**Implementation**: `TicketMirror.Write`
**Behaviour**: rewrites exactly `ticket.md`, `acceptance-criteria.md`, `raw.json` and
`attachments/`. Creates `notes/` empty on first sync and never writes to it again. There is no
recursive delete anywhere in the type, so anything else a user puts in the directory survives **by
construction** rather than by a rule to remember — the discipline `WS-007` applies to skill paths.
**Edge cases**: `attachments/` is emptied file by file, never by deleting the directory. Two
attachments sharing a file name both survive, suffixed — Azure keys them by GUID, so one work item
can legitimately carry two files called `captura.png`.
**Frontend dependency**: none; this is for the user and for the AI to read as files.

### WI-004 Attachments are downloaded and relinked
**Implementation**: `TicketSync.DownloadAsync`, `TicketMirror.Relink`
**Behaviour**: every `AttachedFile` relation is fetched within a 16 MB per-sync budget and the
`<img>` sources in the rendered Markdown are rewritten to the local copies.
**Edge cases**: an attachment that fails or does not fit is **named in `ticket.md`** rather than
silently absent — a screenshot the model cannot see is a fact worth stating. One failed attachment
never fails the sync.

### WI-005 Which account a project's tickets come from
**Implementation**: `TicketAccounts.Resolve`
**Behaviour**: the organisation is `workspaces.ado_org` → `projects.ado_org` → the single configured
connection → `none`. The board project follows the same "explicit choice wins" order:
`workspaces.ado_project` → `projects.ado_project`. Neither is inferred from the repository once a
workspace has chosen: a board can live in a different organisation from the code, which is what
having work and personal projects in one install looks like.
**Edge cases**: with nothing to decide the organisation, the answer is `none` and the UI must ask.
Picking the first connection would read the wrong board and show an empty list. A malformed
`ado_connections` setting reads as no connections rather than throwing. An unknown project id throws
— that is a caller error, not a missing account.
**The board project needs a column of its own, and this is the defect that proved it.** It used to
come only from `projects.ado_project`, which is filled when the repository is linked to an Azure
repository. A repository hosted on GitHub has none — so the organisation resolved, the module
rendered, and the ticket picker then failed with *"choose an account in Settings"*: the very thing
the user had just done. The two are written and cleared as a pair, because a project name without
the organisation it was listed from addresses nothing.
**Frontend dependency**: `TicketAccount.source`, which the UI branches on;
`settings.ticketAccountProject`, whose list comes from the existing `ado_list_projects` command.

### WI-023 One review panel, two axes
**Implementation**: `review_changes` (`TicketCommands.ReviewAsync`), `AiTurn.AnalyzeChangesAsync`, `TicketReview.RunAsync`, `Ai/ReviewScope.cs`, `AnalyzeSection`, `aiPanelStore`
**Behaviour**: a review of local changes is described by two independent choices — **which diff**
(`scope`: the uncommitted tree, or everything the branch contributes over a base) and **whether the
work item is judged too** (`withTicket`). Each combination keeps its own prompt, routing key and
storage:

| Scope | Ticket | Diff | Prompt | Task | Stored in |
|---|---|---|---|---|---|
| working | no | `Diff.Working` | `analyze_template` | `analyze` | `job_history` |
| branch | no | `Diff.BranchContribution` | `analyze_template` | `analyze` | `job_history` |
| working | yes | `Diff.Working` | `ticket_review_standard` | `ticket_review` | `ticket_review_runs` |
| branch | yes | `Diff.BranchContribution` | `ticket_review_standard` | `ticket_review` | `ticket_review_runs` |

**Edge cases**: the two axes used to be welded together — the pre-commit analysis was always the
working tree and never the ticket, the ticket review always the whole branch — so only two of the
four existed. The one that was wanted and missing is a whole-branch review with **no** ticket:
looking over what you have before opening a pull request, in a repository that keeps no tickets.
**A dispatcher, not a third implementation.** `review_changes` chooses between the two orchestrations
and hands each its scope; both bodies stay in the feature that owns them, so `AI-024`'s refusal rules
were not rewritten. It is registered under `Tickets/` rather than `Ai/` because `Tickets/` already
depends on `Ai/`, and registering it the other way round would close a cycle between two features.
**The scope reaches the model as a `SCOPE:` line**, not through the prompt: `analyze_template` is a
user-editable setting whose built-in text says "UNCOMMITTED changes", so anyone who had edited theirs
would have been describing the wrong diff.
**Frontend dependency**: `reviewChanges`, `aiPanelStore.scope` / `.withTicket`.

### WI-024 Judging uncommitted work against criteria carries a caveat
**Implementation**: `ReviewScopes.CriteriaCaveat`
**Behaviour**: with `scope: working` **and** a ticket, the payload carries an explicit instruction:
the branch's earlier commits are not being shown, so absence of evidence is not evidence of absence —
answer `no verificable`, never `no cumple`. Empty for a branch scope, which has the evidence.
**Edge cases**: this is the combination a user asked for most directly, and it is the only one with a
defect of its own. With three commits done and something pending, the model sees only what is pending
and reports met criteria as unmet — **systematically**, not as the occasional false positive that was
explicitly accepted. A verdict wrong in that direction discredits the whole table. The prompt already
carries the `no verificable` doctrine (`WI-012`); the caveat is what activates it for this scope.
`AiOperationsTests.Judging_only_uncommitted_work_against_a_ticket_carries_its_caveat` asserts it is
present for one scope and absent for the other.

### WI-026 A ticket that does not describe the change is said so, first
**Implementation**: `DEFAULT_TICKET_REVIEW_STANDARD` (§"Before anything else"), `TicketCoverage.Relevant`, `TicketVerdictPanel`
**Behaviour**: the review judges **relevance before criteria**. When the linked work item has no real
connection to the files the diff touches, it answers `Relevancia: no corresponde`, sets
`Cobertura: no verificable`, does **not** grade the criteria, and names what the ticket is about
against what the diff is about. The panel shows that instead of the table.
**Edge cases**: a branch can be linked to the wrong work item, and a criterion generic enough —
*"if the key exists, update it"* — matches almost any code that talks to a database. A user linked a
fixture ticket from another project; one sentence of it matched a real `INSERT … ON CONFLICT DO
UPDATE` in the diff, and the review answered `cumple` at 100/100 off that coincidence. Neither
verdict is honest there: `cumple` claims work that was never aimed at the ticket, and `no cumple`
blames a developer for not implementing somebody else's requirement.
**An absent answer reads as relevant**, in both parsers and in the stored form — reviews written
before the question existed keep their meaning, and a model that skipped the line does not have its
verdict discarded. Only an explicit `no corresponde` disowns a ticket.
**Frontend dependency**: `TicketCoverage.relevant` / `.relevance`, `tickets.notRelevant`.

### WI-025 "No job" only means "one is coming" where one is
**Implementation**: `AnalyzeSection` (`autoStartEligible`)
**Behaviour**: the panel shows its spinner while a run is in flight, and also when no job exists yet
**but the auto-start is about to fire** — which is only the cheapest combination (working tree, no
ticket), on a fresh open, with something uncommitted.
**Edge cases**: the old condition was `job?.status === "running" || !job`, and its second half was
true only because a run always was on its way — the section started one when it mounted. The moment a
combination exists that does not auto-start, that reading leaves the panel spinning for ever. The
premise is now named rather than assumed, which is the single change made to a state machine whose
other rules each came out of a real failure.

### WI-021 A ticket on screen always says which branch's work it is
**Implementation**: `TicketStore.List`, `TicketWithLinks`, `TicketDetail`, `WorkItemsView`, `ticketStore.load`
**Behaviour**: `list_tickets` takes a **project** and returns each of its tickets with the
`(project, branch)` pairs it is linked to, project **name** included. The detail pane prints that
under the title — in the accent colour when it matches the open repository and branch, muted and
naming where it does belong when it does not. Every row carries the same, up to two links and then a
`+N`. Loading a project sets the selection to that branch's ticket, or to nothing.
**A link outlives the branch.** Nothing deletes a row from `ticket_links` when a git branch is
deleted — the only `DELETE` is `Unlink`, the explicit button. A merged branch is deleted as a matter
of course, and the record of what it was work for is precisely what you want afterwards. Branches
that no longer exist are **not** marked as such: on a repository of any age most of them are gone,
and an advisory on nearly every row is noise that actions nothing.
**Edge cases**: the list was workspace-wide first, and using it settled the question — the module
answers "what is this repository working on", and mixing in another repository's tickets answers
something this view never asked. `load` used to leave
`selectedId` alone, so switching repositories left the previous repository's ticket open in the
detail pane with **nothing on screen saying so**; a user hit it and asked whether that was right. It
was not: the pre-commit review judges against *this* branch's ticket, so a pane that shows another
one with the same face invites reading an acceptance criterion that does not apply.
`SELECT DISTINCT` was what threw the branch away — the join already produced a row per link — so the
query now groups instead of collapsing, and a ticket worked on from two branches comes back as one
entry with two links rather than one link chosen arbitrarily.
**Frontend dependency**: `TicketWithLinks`, `tickets.linkedHere`, `tickets.linkedElsewhere`.

### WI-019 A pasted address names its own board, and wins
**Implementation**: `ticketStore.link`, `ticketStore.preview`, `LinkTicketModal`, `preview_ticket`
**Behaviour**: `link` takes a `TicketAddress` (`org`, `project`, `externalId`). The organisation and
project resolve address → workspace account → **a visible error**. A work item's URL therefore links
on its own, including one belonging to a different project or organisation from the workspace's, with
no reconfiguration and no confirmation step: the dialog shows which board it belongs to, so the choice
is seen rather than asked about.
**Edge cases**: this replaced an early `return` with no toast. A repository hosted on GitHub has no
`projects.ado_project`, so the account had no board, and pasting the address of a real work item made
the link button do **nothing at all** — no error, no row, no explanation. The URL's own organisation
and project were parsed correctly the whole time (`WorkItemLink.Parse`, pinned against the exact
address that failed) and then discarded one layer up.
`ticketStore.test.ts`'s *"with nothing to address a board it says so instead of doing nothing"* is the
test of that defect: restoring the silent return fails it.
**Frontend dependency**: `preview_ticket` — a batch of one id over `SummaryFields`, deliberately not
`sync_ticket`, which writes the cache, rewrites the mirror and downloads up to 16 MB of attachments.
Debounced 350 ms because a bare id parses on its first digit; the bound is measured rather than
assumed by
`AzureBoardsEndToEndTests.A_single_work_item_previews_fast_enough_to_resolve_while_typing`.

### WI-020 The board lists are a way to find a ticket, not a condition on it
**Implementation**: `LinkTicketModal`
**Behaviour**: the dialog leads with the address field; the current-sprint and assigned-to-me lists
sit below it under a heading that says what they are. Only that lower half needs a configured board,
and only that half says so when there is none.
**Edge cases**: the previous layout led with the two lists and hid pasting inside the filter box,
which read as *"only tickets in the current sprint can be linked"* — a user said so. They cannot be:
`ticket_links` is keyed `(project_id, branch)` and knows nothing about iterations. The lists stay
because a board holds thousands of work items and a flat search over them is a worse tool than the
browser already open — one real board measured 46 rows in its sprint. What changed is which one is
the path and which is the fallback. Before, a missing account put `no-account` where the rows would
be and made the whole dialog look broken, pasting included.

### WI-018 A settings change re-resolves the account
**Implementation**: `ticketStore.refreshAccount`, `WorkspaceTicketAccounts`
**Behaviour**: choosing an organisation or a board project re-reads `resolve_ticket_account` for the
project the module last loaded, so the work-items view stops asking the moment it is answered.
**Edge cases**: the store remembers that project id rather than reaching into the workspace store —
the settings row knows which *workspace* changed, not which repository is open. Without this the
account was read once, when the view mounted: the settings panel opens **over** that view rather than
replacing it, so nothing unmounted, no effect re-fired, and the answer stayed stale until the app was
restarted. Changing the organisation clears the project in the same write, because a project name
belongs to the organisation it was listed from.

### WI-006 The branch heuristic is a suggestion
**Implementation**: `TicketBranchRef.Detect`
**Behaviour**: recognises `AB#1234`, a Jira key (upper case only), and a leading number on the
branch's own last segment.
**Edge cases**: upper case is required for a Jira key because accepting lower case matches `utf-8`
in `feature/utf-8-encoding`. A date-led branch such as `release/2025-cleanup` resolves to work item
2025 — an accepted false positive, pinned by a test, because nothing in the name separates a year
from a work-item number and rejecting four-digit ids would reject the common real case.
**Frontend dependency**: `suggestTicketForBranch`. `TicketStore.ForBranch` answers only from the
explicit link, so no review is ever judged against a ticket nobody chose.

### WI-007 Criteria extraction has two modes and a floor
**Implementation**: `TicketCriteriaReader.Read`
**Behaviour**: walks the configured field order — `ticket_criteria_fields:{org}:{project}`,
defaulting to `Microsoft.VSTS.Common.AcceptanceCriteria` then `System.Description` — and takes the
first field that clears the substance floor and is not a template. A field carrying a list yields
`mode: "list"` with items numbered `AC-1…AC-N`; anything else yields `mode: "prose"` with the
Markdown whole. Nothing usable yields `mode: "none"`.
**Edge cases**: the floor is 25 characters of **tag-stripped** text
(`TicketHtml.SubstanceLength`) — `<div><b>-</b> </div>` is 20 characters of markup and one of
content, and counting the former calls an empty box a requirement. Prose is never split into
numbered criteria: doing it by regex cuts rules in half and the model then reports failures that
belong to the splitting. A nested bullet extends the criterion above it rather than becoming its
own, because a sub-case qualifies the rule it sits under.
**Frontend dependency**: `TicketCriteria.mode` and `.field` — the picker shows which field a
ticket's requirements would come from before anything is linked.

### WI-008 A field repeated across tickets is a form, not an answer
**Implementation**: `TicketCriteriaReader.IsTemplate`, `TicketStore.OthersOfType`
**Behaviour**: a candidate field whose tag-stripped text matches the same field on another cached
ticket of the same board and type is skipped. Compared against at most 20 others.
**Edge cases**: with no other ticket cached the comparison says nothing and excludes nothing —
guessing without a corpus would drop a real requirement the first time a board is used, which is
exactly when nobody would suspect the extraction. A cached payload that will not parse is ignored.

### WI-009 Sync runs on three triggers, never on a timer
**Implementation**: `TicketSync.RunAsync`
**Behaviour**: on link, on an explicit refresh, and best-effort immediately before a review so the
criteria being judged are current. A background poll would spend a PAT's rate budget on tickets
nobody is looking at.
**Edge cases**: the mirror write is best-effort (`IOException`/`UnauthorizedAccessException`
swallowed) for the same reason `SkillSync.TryRun` is — a full disk must not turn a successful fetch
into a failed command. What the app reads is the cache; the mirror is for people and for the AI.
An `external_id` that is not a number is rejected before any request.

### WI-010 A work item address is accepted in four shapes
**Implementation**: `WorkItemLink.Parse`
**Behaviour**: the work-item page on `dev.azure.com` or `{org}.visualstudio.com`, any board URL
carrying `?workitem=`, and a bare id or `AB#`. Organisation and project come back null for a bare
id, and the caller fills them from the workspace.
**Edge cases**: it does **not** reuse `PrLink`'s splitter, which discards the query string — the
taskboard URL is the one most likely to be in the clipboard, and it carries the id there. A Jira key
is refused rather than read as an Azure number.
**Frontend dependency**: `resolveTicketLink`.

### WI-011 The ticket review is its own task and its own prompt
**Implementation**: `AiRouting.Tasks`, `AiRouting.Judging`, `Settings.SeededPrompts`, `Settings.DefaultWorkspacePrompt`, `Ai/Prompts/DEFAULT_TICKET_REVIEW_STANDARD.txt`
**Behaviour**: `ticket_review` is the ninth routing key, so the model that judges a branch against a
work item can differ from the one that reads a pull request — usually a larger one, because the
question is harder. The methodology is a **per-workspace prompt** (`ticket_review_standard`), seeded
by `Migrations.BackfillWorkspacePrompts` and edited from Settings → Review → Ticket, following
`review_standard` rather than the shared `analyze_template`: it is a review, it carries the whole
finding contract, and criteria conventions differ per team.
**Edge cases**: `Settings.DefaultWorkspacePrompt` needs an **explicit arm** for the new kind. Its
catch-all returns the PR methodology, so without one "restore default" would hand back a prompt that
never mentions a work item and never emits the verdict sections — failing nowhere, and reading as the
model refusing rather than as a settings bug.
`SettingsTests.The_ticket_review_standard_does_not_fall_through_to_the_pr_one` is that guard.
It joins `AiRouting.Judging` for a reason `analyze` and `review` do not have: it is asked whether a
criterion is *met*, and a criterion is regularly satisfied by code the diff does not touch, so
without `Read`/`Grep`/`Glob` the honest answer would be `no verificable`.
**Frontend dependency**: `AI_TASKS` (`ticket_review`), `settings.reviewTabTicket`.

### WI-012 The two review standards share their finding format byte for byte
**Implementation**: `Ai/Prompts/DEFAULT_TICKET_REVIEW_STANDARD.txt`, `PromptsTests`
**Behaviour**: everything from `## Review lenses` to the end of `DEFAULT_PR_REVIEW_STANDARD.txt` —
the lenses, the taxonomy, the discard list, the A–E ratings, the Quality Gate and the whole output
format — appears in the ticket standard **identically**, 5 228 characters of it.
**Edge cases**: two copies of a contract two parsers match on is a real hazard, and rewording one for
clarity is the plausible mistake. It would fail nowhere: the model would emit findings the renderer
cannot read and the review would render as one wall of prose. So
`PromptsTests.The_two_review_standards_share_the_finding_format_verbatim` asserts the identity
instead of trusting it. What the ticket standard adds is its own — the ticket block, the
anti-assumption prohibitions and the two closing sections of `XLANG-016`.

### WI-013 A ticket review is stored in its own table
**Implementation**: `TicketReviewStore`, `ticket_review_runs`
**Behaviour**: one row per finished review, holding the markdown, the parsed criteria, the coverage
word, the findings in **the same JSON shape as `review_runs.findings`**, and the diff it judged.
**Edge cases**: not a row in `review_runs`, and the reason is structural: that table's `pr_id` is
`NOT NULL` and its index is `(project_id, pr_id, created_at)`. A pre-commit review has no pull
request, so it would need a fake id — corrupting the index and every reader that treats the column as
a real PR — or a nullable column, which is a schema change to the busiest table in the file. The
criteria are stored **parsed**: they came from a model's answer, and re-reading that text later with
a changed parser would silently rewrite history. A row whose payload will not parse still renders its
markdown, so one bad row cannot take the history list down.
**Frontend dependency**: `TicketReviewResult`, `list_ticket_reviews`.

### WI-014 The review reads the branch's whole contribution, against a base you chose
**Implementation**: `TicketReview.RunAsync`, `Diff.BranchContribution`, `renderer/src/lib/branches.ts`
**Behaviour**: the diff is the merge base of the chosen base branch against the current **working
tree** — one comparison, so a file touched in a commit and again uncommitted appears once. The prompt
says so, because "this change is already committed" is not a distinction the model can make from the
diff and it must not try.
**Edge cases**: there is no default base branch anywhere in this app. `preferredBaseBranch` guesses
one from `PREFERRED_TARGETS`, extracted verbatim out of `CreatePrModal.tsx` along with its exact
behaviour — the first *branch* the repository lists whose name is in the set, not the first name in
the set — and the guess is shown in an editable control. Reviewing against the wrong base produces a
diff that is not the branch's contribution, and nothing in the answer would say so.
**Frontend dependency**: `ticketReview.against`.

### WI-015 The pre-commit gate offers, once, and never blocks
**Implementation**: `ChangesPanel.handleCommit`
**Behaviour**: the second gate after the secret scan. When the branch has a linked ticket, the first
commit attempt on that branch offers to run the review; either answer commits when the user says so.
**Edge cases**: offered **once per branch**, tracked in a ref, whichever way it is answered. The
point is to be there at the moment acting on the answer is still cheap, not to stand between the user
and every commit — and with false positives explicitly accepted, a gate that could not be walked
through would be a gate that gets disabled. Unlike the secret scan's modal, the safe-looking button
is the review and the other one is not styled as dangerous: a review that finds nothing is the common
case.

### WI-016 The user's notes reach the review
**Implementation**: `TicketMirror.ReadNotes`, `AiOperations.ReviewBranchAgainstTicketAsync`
**Behaviour**: the `.md` / `.txt` files in the mirror's `notes/` are read and passed to the model
under `USER NOTES ON THIS TICKET:`, within a 20 000-character budget.
**Edge cases**: reading is not writing — `WI-003` still holds, and nothing here creates, deletes or
modifies. Text extensions only: a screenshot dropped in there is not something to paste into a
prompt. A note that will not read is skipped rather than failing the review. What a ticket leaves
unsaid is exactly what a review judging "does this deliver it" is missing, which is why the directory
that exists for the user is also the one the model is told about.

### WI-017 The ticket block has its own prompt budget, and the criteria have none
**Implementation**: `AiOperations.ReviewBranchAgainstTicketAsync` (`MaxTicketChars`, `MaxTicketNotesChars`)
**Behaviour**: the ticket's prose is capped at 40 000 characters and the notes at 20 000; the diff
keeps the 250 000 `PromptDiff` already budgets. The **acceptance criteria are never capped**.
**Edge cases**: before this, the diff spent a deliberate budget and the ticket was concatenated after
it with no ceiling at all — a work item with a long refinement thread would have starved the branch's
own contribution out of the prompt. The criteria are exempt because they are what the change is being
judged against: truncating them turns "the model did not check AC-7" into a finding about the work
rather than about the prompt.

## Test coverage

`tests/CodeFlow.Tests/Tickets/` — `TicketPathsTests`, `TicketBranchRefTests`, `TicketHtmlTests`,
`TicketCriteriaReaderTests`, `TicketStoreTests`, `TicketAccountsTests`, `TicketMirrorTests`,
`TicketCommandsTests`, `TicketVerdictTests`, `TicketReviewStoreTests`.
`tests/CodeFlow.Tests/Providers/` — `AzureWorkItemClientTests`, `WorkItemLinkTests`.
`tests/CodeFlow.Tests/Ai/` — `PromptsTests`.
Renderer — `state/ticketStore.test.ts`, `lib/parseTicketVerdict.test.ts`, `lib/branches.test.ts`.

Two are load-bearing.
`TicketMirrorTests.Anything_the_user_put_in_the_directory_survives_a_resync`: this is the first
feature that writes into a directory a person also uses, and that is the promise made to them.
`TicketVerdictTests.ParseFindings_reads_the_same_findings_with_or_without_the_verdict_section`: two
contracts now share one document, and this is what says the older one is untouched.

## The end-to-end fixtures

`tests/CodeFlow.Tests/Tickets/AzureBoardsEndToEndTests.cs` runs against a real organisation when
`CODEFLOW_E2E_ADO_ORG` is set, reading its PAT from the OS keychain — no credential lives in this
repository or in a build. The board it was developed against holds three work items, created for it
and left in place, each covering one branch of `WI-007`:

| Id | Type | Shape | Exercises |
|---|---|---|---|
| 1 | Bug | title only | `mode: "none"` — nothing carries requirements |
| 2 | User Story | `AcceptanceCriteria` as a `<ul>` | `mode: "list"`, numbered `AC-1…AC-3` |
| 3 | User Story | criteria hold `<div><b>-</b></div>`, spec in the description | `mode: "prose"` and the substance floor |

All three sit on the team's `current` iteration, which is what makes the taskboard route testable.
Deleting them makes five of the seven tests skip, with a reason naming what is missing.

## Markers raised

`VERIFIED-LIVE` (2026-08-11): the read path ran end to end against a real organisation — project
listing, WIQL with the mandatory clause, the 200-id batch, the team → iteration → work items route,
a single work item with its fields and relations, the comments endpoint on its preview contract,
and a full `TicketSync` writing a mirror whose `ticket.md` came back free of markup and entities.

`UNVERIFIED`: `AzureWorkItemClient.ListTypeFieldsAsync` and `GetAttachmentAsync` are covered against
a fake transport only — the fixture board has no attachments. Nothing has yet run through the app's
own UI rather than through a test.

`UNVERIFIED`: **no ticket review has yet been run against a real model.** Every part of the pipeline
is covered — the prompt's shared block byte for byte, both verdict parsers against the exact text the
prompt asks for, the store round trip, the routing key, the seeded prompt — but what a model actually
emits into `## VERIFICACIÓN DE CRITERIOS DE ACEPTACIÓN` is the one thing a test cannot assert. Both
parsers are tolerant and both default an unreadable verdict to `no verificable`, so the failure mode
is a missing table rather than a wrong one; the measurement still has to be made.
