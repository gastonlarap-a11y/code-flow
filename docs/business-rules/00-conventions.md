# 00 — Conventions

How to read and how to extend the business-rules inventory. Every other document in
`docs/business-rules/` follows the rules stated here.

## What this inventory is

**This is the specification of CodeFlow's behaviour** — the only one the application has.
`src/CodeFlow.App/` (with `shell/` and `renderer/`) is the implementation that must satisfy it.
The documents describe **behaviour, not implementation strategy**: how a rule is achieved in C# is
the code's business, not this inventory's.

Where the code and a document disagree, that is a defect in one of them and must be resolved, not
left standing. A behaviour that is deliberately preserved for compatibility is recorded in
`91-known-bugs.md`; a behaviour nobody has verified against a real external system is recorded in
`90-ambiguities.md`.

## Language

All prose is English.

Content that is a byte-level contract is quoted **verbatim in its original language** and
never translated. That covers the nine Spanish prompt constants in
`src/CodeFlow.App/Ai/Prompts/`, the Spanish/emoji review finding format, every regular
expression, every error-string prefix and every keychain key format. The English-only rule is
explicitly exempted here — translating any of it changes what the model emits and breaks two
parsers at once. See `13-cross-language-contracts.md`.

## Ownership

Every implementation file is owned by **exactly one** document. A document's `## Scope` section
lists its files:

`
- `src/CodeFlow.App/Git/Stash.cs`
`

`01-ipc-surface.md` owns the *contract surface* — the command and event tables. The command
registration files themselves are owned by their domain document, which describes what calling
each command actually does. A domain document never restates a command's parameters or return
type; it links to `01`.

## Rule format

Every rule is written in one shape:

`markdown
### GIT-014 Stash rename reorders the stash stack
**Implementation**: `src/CodeFlow.App/Git/Stash.cs`
**Behaviour**: <imperative, present tense — what it does>
**Inputs / outputs**: <params, return shape, exact error strings the frontend keys off>
**Edge cases**: <empty, missing, conflicting, concurrent>
**Frontend dependency**: <the TS caller that relies on this, or "none">
**Markers**: <none, or one or more markers from the table below>
`

Rule ids use a per-document prefix (`BOOT`, `STORE`, `GIT`, `AI`, `PROV`, `REVIEW`, `API`,
`WS`, `SEC`, `FILE`, `DBG`, `XLANG`) plus a zero-padded sequence. Ids are stable once
written — later documents reference them.

## Markers

| Marker | Meaning | Aggregated into |
|---|---|---|
| `UNVERIFIED` | The path compiles but has never been executed against a real external system. Port it, but do not treat it as proven behaviour. | `90-ambiguities.md` |
| `AMBIGUOUS-####` | The source does not determine the behaviour. A decision is required before porting. **Never resolved by guessing.** | `90-ambiguities.md` |
| `BUG-####` | Defect in CodeFlow 1.7.2. Documented, **not fixed** — the frontend may depend on it. Always states the suspected-correct behaviour for context. | `91-known-bugs.md` |
| `DIVERGENCE` | Deliberate departure from what a reader would expect, which must be preserved (hooks never firing, the watcher not being a plain debounce, `C:\CodeFlow`). Not a bug — a "do not investigate this" flag. | — |
| `VERBATIM` | Content transcribed byte-for-byte that must never be rewritten (prompts, regexes, keychain keys, the review markdown format). | `13-cross-language-contracts.md` where it crosses languages |
| `DEAD` | Registered command with zero call sites. One known instance: `debug_is_running`. | `01-ipc-surface.md` |

### Numbering

Marker ids are `KIND-PREFIX-letter` — `AMBIGUOUS-GIT-a`, `BUG-API-b` — where `PREFIX` is the
owning document's rule prefix. Documents were written concurrently, so this scheme existed to
stop parallel authors colliding on a shared counter.

The merge pass originally planned to renumber these into a flat global sequence
(`AMBIGUOUS-001`, …). **It did not, deliberately.** Every prefix has exactly one owning
document, so all 62 ids were already globally unique; renumbering would have replaced a
self-describing id with an opaque one and rewritten thirteen documents for no gain. The
domain-scoped ids are final. `PROV` and `XLANG` appear in two documents each, but only as
cross-references from `07-review-pipeline.md` and `09-workspace-scoped.md` to the owning
document's ledger — never as a second definition.

The ledgers (`90-ambiguities.md`, `91-known-bugs.md`) list every marker once, with its owning
document. An inline marker and its ledger row always agree because neither was rewritten.

## The two standing prohibitions

1. **Do not guess.** Where the source does not determine the behaviour, write
   `AMBIGUOUS-*` and describe what is unclear. An unmarked guess is a defect in this
   inventory, because the port has no way to tell it apart from an established rule.
2. **Do not fix.** Where CodeFlow 1.7.2 is wrong, write `BUG-*` and describe both
   what it does and what it probably should do. The frontend may depend on the defect.

## Source of truth for counts

| Fact | Value | How it was established |
|---|---|---|
| implementation files | 65 | `find `src/CodeFlow.App/` -name '*.rs' \| wc -l` |
| the sidecar lines | 22 730 | `wc -l` over the same set |
| Commands registered | 236 | 219 from the port's own parse (`analyze_working_changes` is gone), plus the 17 of `Tickets/TicketCommands.cs` |
| Commands defined | 236 | same set, in both directions |
| Commands invoked by the frontend | 232 | 216 plus the same 16 wrappers |
| Dead commands | 1 | `debug_is_running` |
| Event names | 13 | `.emit(` call sites |
| Event (name, producer) pairs | 19 | the four `debug:*` and the two `api:*` names have two producers each |
| Emit call sites | 23 | — |
| Rows in the event table | 20 | 19 pairs, with `git:progress` split into its stdout and stderr sites |
| Tables | 21 | `CREATE TABLE IF NOT EXISTS` in `src/CodeFlow.App/Storage/Schema.cs` |
| extracted case functions | **133** across 25 files | 128 ` + 5 ` |
| Secret-scan rules | **15** | 14 `new Rule(...)`(…)` literals plus the appended `generic` rule, `src/CodeFlow.App/Security/SecretScan.cs` |

Two of these corrected an earlier figure and are recorded so they are not re-litigated:

- The secret-scan count is **15**. A naive `grep -c '`new Rule(...)`('` returns 14 because the
  `generic` rule is appended separately. the count below is correct.
- The test count is **133**, not the 131 quoted during Phase 0 scoping and not the 128 this
  document first recorded. `grep -c '#\[test\]'` returns 128 and misses the five
  ` functions in not implemented (deferred). Counting both attributes gives 133 distinct
  `(file, function)` pairs across 25 files.

  Four function names are each used in two different engine files
  (`a_quota_message_gets_the_marker`, `a_successful_run_returns_stdout_as_the_reply`,
  `empty_output_on_a_clean_exit_is_an_error_not_a_blank_reply`, `surfaces_the_failure_detail`)
  — the engines mirror one another's error/quota contract deliberately. A test inventory keyed
  on function name alone therefore undercounts; key on `(file, function)`.
