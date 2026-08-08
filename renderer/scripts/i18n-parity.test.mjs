import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "vitest";

/**
 * The two locales carry the same keys.
 *
 * TypeScript already stops a component from using a key that does not exist, because
 * `TranslationKey` is `keyof (typeof translations)["en"]`. What it cannot see is a key present in
 * `en` and missing from `es`: `render()` falls back to the English string, so the app looks right
 * in English and comes out half-translated in Spanish, with no error anywhere.
 *
 * Parsed as text rather than imported: it needs no module resolution at all, which is why it was
 * the one renderer test that predated Vitest, and why it is still plain `.mjs`.
 */

const source = readFileSync(
  fileURLToPath(new URL("../src/lib/i18n/translations.ts", import.meta.url)),
  "utf8",
);

/** Keys are the lines indented exactly four spaces inside a locale block. */
function keysBetween(start, end) {
  const block = source.slice(start, end);

  return new Set([...block.matchAll(/^ {4}"([^"]+)":/gm)].map((match) => match[1]));
}

const englishAt = source.indexOf("\n  en: {");
const spanishAt = source.indexOf("\n  es: {");

test("both locale blocks are where the parser expects them", () => {
  // If this file is ever restructured, the checks below would silently compare two empty sets and
  // pass. This is what stops that.
  assert.ok(englishAt >= 0, "no `en:` block found in translations.ts");
  assert.ok(spanishAt > englishAt, "no `es:` block found after `en:` in translations.ts");
});

test("every English key has a Spanish translation", () => {
  const english = keysBetween(englishAt, spanishAt);
  const spanish = keysBetween(spanishAt, source.length);

  assert.ok(english.size > 1000, `only ${english.size} English keys parsed — the format changed`);
  assert.deepEqual([...english].filter((key) => !spanish.has(key)), []);
});

test("Spanish carries no key English does not", () => {
  const english = keysBetween(englishAt, spanishAt);
  const spanish = keysBetween(spanishAt, source.length);

  // A key only in `es` is unreachable: `TranslationKey` is derived from `en`, so nothing can ask
  // for it. It is dead weight, and usually a rename that only landed on one side.
  assert.deepEqual([...spanish].filter((key) => !english.has(key)), []);
});

test("no key is declared twice in the same locale", () => {
  // A duplicate is legal TypeScript — the later one silently wins — and is how a translation gets
  // "fixed" in a place nobody reads again.
  for (const [name, start, end] of [
    ["en", englishAt, spanishAt],
    ["es", spanishAt, source.length],
  ]) {
    const all = [...source.slice(start, end).matchAll(/^ {4}"([^"]+)":/gm)].map((m) => m[1]);
    const duplicates = all.filter((key, index) => all.indexOf(key) !== index);

    assert.deepEqual(duplicates, [], `duplicate keys in ${name}`);
  }
});
