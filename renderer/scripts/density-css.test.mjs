import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "vitest";

/**
 * The tree row height exists in two places, and they have to agree.
 *
 * `lib/ui/density.ts` owns it; `index.css` declares `--cf-row-height` as well, because
 * `densityStore.init()` has to wait on an IPC round trip and the rows are painted before it
 * returns. If the two drift, the trees render at one height and then jump to another on load —
 * which looks like a bug in the virtualizer and is not.
 *
 * Read as text rather than imported, for the same reason the i18n parity check is: this file needs
 * `node:fs`, and `renderer/src` is browser code compiled without Node types. Keeping it here keeps
 * `tsc --noEmit` honest about what the app itself is allowed to reach for.
 */

const read = (path) => readFileSync(fileURLToPath(new URL(path, import.meta.url)), "utf8");

test("index.css seeds --cf-row-height with the default density", () => {
  const density = read("../src/lib/ui/density.ts");
  const css = read("../src/index.css");

  const defaultStep = /DEFAULT_DENSITY:\s*TreeDensity\s*=\s*"(\w+)"/.exec(density)?.[1];
  assert.ok(defaultStep, "could not find DEFAULT_DENSITY in density.ts");

  const table = /DENSITY_PX\s*=\s*\{([^}]*)\}/.exec(density)?.[1];
  assert.ok(table, "could not find DENSITY_PX in density.ts");

  const expected = new RegExp(`\\b${defaultStep}:\\s*(\\d+)`).exec(table)?.[1];
  assert.ok(expected, `DENSITY_PX has no entry for the default step "${defaultStep}"`);

  const declared = /--cf-row-height:\s*(\d+)px/.exec(css)?.[1];
  assert.ok(declared, "could not find --cf-row-height in index.css");

  assert.equal(
    declared,
    expected,
    `index.css paints rows at ${declared}px before boot, but the default density ` +
      `("${defaultStep}") is ${expected}px — the trees would jump on load`,
  );
});
