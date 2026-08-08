/**
 * Quick-open matching: the ranking behind "type three letters, get the file you meant".
 *
 * Lifted out of `editor/FilePalette.tsx` when the command bar's `@` scope started needing the same
 * ranking. It was untestable where it was — `.test.tsx` is never collected — and two copies of a
 * scoring function drift the moment one of them is tuned.
 */

/**
 * Score `text` against `query`; lower is better, `null` means it does not match at all.
 *
 * Three tiers, in the order a person means them:
 *  1. a substring of the *last segment* — the filename, which is what is almost always intended;
 *  2. a substring anywhere in the whole string, ranked below every filename hit;
 *  3. a subsequence, so "edvw" still finds `EditorView.tsx`, penalised by how spread out it is.
 *
 * `query` is expected lowercase and trimmed; the caller does that once instead of per candidate.
 */
export function fuzzyScore(text: string, query: string): number | null {
  if (!query) return 0;
  const haystack = text.toLowerCase();
  const name = haystack.slice(haystack.lastIndexOf("/") + 1);

  const inName = name.indexOf(query);
  if (inName >= 0) return inName;
  const inPath = haystack.indexOf(query);
  if (inPath >= 0) return 100 + inPath;

  let cursor = 0;
  let gaps = 0;
  for (const char of query) {
    const found = haystack.indexOf(char, cursor);
    if (found < 0) return null;
    gaps += found - cursor;
    cursor = found + 1;
  }
  return 1000 + gaps;
}

/**
 * The matching candidates, best first, capped.
 *
 * Ties break on length: with two equal-scoring hits the shorter path is the more specific one.
 * Filtering runs over everything and only the top slice is returned — nobody scrolls a thousand
 * results, they type another letter.
 */
export function rankByFuzzy<T>(items: readonly T[], query: string, key: (item: T) => string, limit: number): T[] {
  const needle = query.trim().toLowerCase();
  const scored: { item: T; score: number; length: number }[] = [];
  for (const item of items) {
    const text = key(item);
    const score = fuzzyScore(text, needle);
    if (score !== null) scored.push({ item, score, length: text.length });
  }
  scored.sort((a, b) => a.score - b.score || a.length - b.length);
  return scored.slice(0, limit).map((s) => s.item);
}
