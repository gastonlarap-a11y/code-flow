import { parseDbmlModel } from "./parse";
import type { DbmlAssistMode } from "../../types/domain";

/**
 * What happens to the AI's answer once it arrives (DBML-016, DBML-017).
 *
 * The mode itself is a wire value and lives in `types/domain.ts`; what is here is the half the
 * sidecar cannot do — deciding whether a rewritten schema is safe to put in front of a person.
 */

/** Whether a mode's answer replaces the document or is only read. */
export function rewritesDocument(mode: DbmlAssistMode): boolean {
  return mode === "edit";
}

/** What changed between the open document and a proposal, in tables. */
export interface DbmlProposalSummary {
  /** Tables the proposal has and the document does not, by `schema.table` key. */
  added: string[];
  /** Tables the document has and the proposal does not. The number worth showing before applying. */
  removed: string[];
  /** Tables present on both sides, changed or not. */
  kept: number;
}

/**
 * A proposal, once it has been checked — never a bare string.
 *
 * Modelled as a union rather than a string plus an error flag because the four outcomes lead to four
 * different screens, and a proposal that must not be applied should not be representable as one that
 * can.
 */
export type DbmlProposal =
  | { kind: "ok"; source: string; summary: DbmlProposalSummary }
  | { kind: "empty" }
  | { kind: "unchanged" }
  | { kind: "invalid"; error: string };

/**
 * Decides whether a rewritten schema can be offered, and what it would change.
 *
 * **Nothing the model returns is trusted to be DBML.** It is asked for the whole document with no
 * prose and no fence, and an engine that answers with an apology or a fragment would otherwise
 * replace a working schema with it. Parsing here — the renderer is where `@dbml/core` is — turns
 * that into a rejected proposal the user never has to undo.
 *
 * The table delta is reported for the same reason at the next level up: a model that returns three
 * tables of seven has obeyed the format and lost the document, which the diff shows only if someone
 * scrolls it. `removed` puts it on screen before the button.
 */
export function checkProposal(current: string, answer: string): DbmlProposal {
  const source = answer.trim();
  if (source.length === 0) return { kind: "empty" };

  const parsed = parseDbmlModel(source);
  if (!parsed.ok) return { kind: "invalid", error: parsed.error };

  if (normalize(current) === normalize(source)) return { kind: "unchanged" };

  // A document that does not parse has no tables to compare against, which is the state the editor
  // is in for most of an edit. An unparseable *original* is not an error here — the proposal is
  // still offerable, and every one of its tables simply reads as added.
  const before = parseDbmlModel(current);
  const previous = new Set(before.ok ? before.model.tables.map((table) => table.key) : []);
  const proposed = parsed.model.tables.map((table) => table.key);
  const proposedKeys = new Set(proposed);

  return {
    kind: "ok",
    source,
    summary: {
      added: proposed.filter((key) => !previous.has(key)),
      removed: [...previous].filter((key) => !proposedKeys.has(key)),
      kept: proposed.filter((key) => previous.has(key)).length,
    },
  };
}

/**
 * Line endings and trailing whitespace normalised, so "unchanged" means what a person means by it.
 *
 * A model that returns the document with `\r\n` or a trailing blank line has changed nothing worth
 * showing a diff for, and offering that as an edit is how a schema acquires a whitespace-only commit.
 */
function normalize(source: string): string {
  return source
    .replace(/\r\n/g, "\n")
    .split("\n")
    .map((line) => line.trimEnd())
    .join("\n")
    .trim();
}
