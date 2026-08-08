/**
 * The stamped footer under an analysis, split into the pieces it is made of.
 *
 * The sidecar writes one line — engine, model, level, when, how long, what it cost, how much of the
 * change it saw, what the findings did — separated by `·` throughout, and `parseAnalysis` lifts it
 * off the end of the text. Shown as one long sentence it reads as small print; split, each piece is
 * a fact you can find. The separator is the contract, so nothing the sidecar puts in a segment ever
 * contains one.
 *
 * The leading 🤖 goes: the chips already sit under a review nobody could mistake for hand-written.
 */
export function runStatSegments(footer: string | null | undefined): string[] {
  if (!footer) return [];

  return footer
    .replace(/^🤖\s*/u, "")
    .split("·")
    .map((segment) => segment.trim())
    .filter((segment) => segment.length > 0);
}
