import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "vitest";
import { componentFiles, nativeTitles, pixelTextSizes, readSource } from "./ui-conventions.mjs";

/**
 * The two design-system rules that can be checked without a DOM, checked.
 *
 * `.claude/rules/renderer.md` states both — no `text-[Npx]` outside the type scale, and an
 * icon-only control is an `IconButton` (whose label feeds a real tooltip, not a native `title`) —
 * and the UX redesign migrated the app onto them one area at a time rather than by find-and-replace.
 * A rule enforced only by review is a rule that comes back: this makes a migrated file stay
 * migrated, and CI is where that is noticed.
 *
 * **There is no exemption list any more.** Each phase deleted its own entries and phase D deleted the
 * last of them, so the type-scale rule now holds across every component with no exceptions. The one
 * survivor is an allowlist, not a backlog — see below.
 *
 * This lives in `scripts/` and reads the files as text, like `i18n-parity` and `density-css` do:
 * `node:fs` has no place in `src/`, which is compiled without Node types on purpose.
 */

const ROOT = fileURLToPath(new URL("../src/components", import.meta.url));

/**
 * The one place a native `title` is correct, and it is not a migration that never happened.
 *
 * `layout/WindowControls.tsx`'s three Windows caption buttons imitate the OS's own chrome, down to
 * keeping the OS's own tooltip; the app's bubble is an app affordance and these three are the
 * window's. The i18n'd `aria-label` beside each one is what accessibility depends on, and the file
 * says so.
 *
 * It named `layout/TitleBar.tsx` until the 2.0 command header replaced that file. The buttons moved
 * out into their own module rather than into the new header: the exemption is about the window's
 * chrome specifically, and keeping it that narrow is what stops it becoming a general licence for
 * whichever component happens to hold them.
 *
 * A second entry here needs the same standard: a reason written in the file it names.
 */
const NATIVE_TITLE_ALLOWED = new Set(["layout/WindowControls.tsx"]);

const files = componentFiles(ROOT);

test("no component picks its own text size outside the scale", () => {
  const offenders = [];
  for (const file of files) {
    const hits = pixelTextSizes(readSource(ROOT, file));
    for (const hit of hits) offenders.push(`${file}:${hit.line} ${hit.value}`);
  }
  assert.deepEqual(
    offenders,
    [],
    "use the type scale (text-badge / text-ui / text-body / text-relaxed / text-title):\n" +
      offenders.join("\n"),
  );
});

test("no control is labelled with a native title attribute", () => {
  const offenders = [];
  for (const file of files) {
    if (NATIVE_TITLE_ALLOWED.has(file)) continue;
    const hits = nativeTitles(readSource(ROOT, file));
    for (const hit of hits) offenders.push(`${file}:${hit.line} <${hit.tag} title=…>`);
  }
  assert.deepEqual(
    offenders,
    [],
    "use Tooltip, or IconButton's required label, instead of the native title:\n" +
      offenders.join("\n"),
  );
});

/** An allowlist naming a file that no longer exists is an exception nobody is taking any more. */
test("the allowlist names files that exist", () => {
  const present = new Set(files);
  const stale = [...NATIVE_TITLE_ALLOWED].filter((f) => !present.has(f));
  assert.deepEqual(stale, [], `allowed files that no longer exist: ${stale.join(", ")}`);
});
