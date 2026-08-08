import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "vitest";
import { AA_CONTRAST, contrastRatio } from "../src/lib/ui/contrast.ts";

/**
 * The status colours are fixed; the surfaces under them are not.
 *
 * `--cf-success`, `--cf-danger` and `--cf-warning` are declared once in `index.css`, while
 * `codeThemes.applyThemeVars()` repaints `--cf-surface-raised` from whichever of the 21 code themes
 * is selected. So "does the danger colour read?" has 21 answers, and picking a shade against the
 * default theme alone is how three tinted light themes ended up below AA without anyone noticing:
 * `tokyo-night-light` measured 3.73:1 for danger where the same token measured 4.83:1 on white.
 *
 * Read as text rather than imported, like `density-css` and `i18n-parity`: this needs `node:fs`, and
 * `renderer/src` is browser code compiled without Node types.
 */

const read = (path) => readFileSync(fileURLToPath(new URL(path, import.meta.url)), "utf8");

/** `--name: light-dark(#aaa, #bbb);` → `{ light, dark }`. */
function statusToken(css, name) {
  const match = new RegExp(`--cf-${name}:\\s*light-dark\\((#[0-9a-f]{6}),\\s*(#[0-9a-f]{6})\\)`, "i").exec(css);
  assert.ok(match, `--cf-${name} is not a light-dark() pair of hex colours`);
  return { light: match[1], dark: match[2] };
}

/** Every theme's raised surface, split by which side of the app it paints. */
function themeSurfaces(source) {
  const light = [];
  const dark = [];
  for (const match of source.matchAll(/id:\s*"([^"]+)"[\s\S]{0,900}?ui:\s*\{([^}]*)\}/g)) {
    const raised = /surfaceRaised:\s*"(#[0-9a-f]{6})"/i.exec(match[2])?.[1];
    if (!raised) continue;
    // White on the surface tells us which side it is: a light theme is closer to white than to black.
    (contrastRatio(raised, "#ffffff") < 2 ? light : dark).push({ id: match[1], raised });
  }
  assert.ok(light.length >= 5 && dark.length >= 5, "expected both light and dark themes to be found");
  return { light, dark };
}

const css = read("../src/index.css");
const themes = themeSurfaces(read("../src/lib/codeThemes.ts"));

/**
 * There is no exemption list any more.
 *
 * `--cf-danger` in the dark theme used to be one: red-400 measured 2.86:1 on `darcula`, and the two
 * neighbouring shades were both wrong — red-300 still missed at 4.16:1, red-200 cleared at 5.46:1
 * but had turned pink. The 2.0 palette settled it with a value between them, off Tailwind's scale,
 * which clears all eleven dark themes at 4.58:1 on the worst. `darcula`, `nord`, `gruvbox-dark` and
 * `dracula` are no longer special, so nothing here treats them as such.
 */

for (const role of ["success", "danger", "warning", "info"]) {
  test(`--cf-${role} reads on every light theme's raised surface`, () => {
    const token = statusToken(css, role).light;
    const failures = themes.light
      .map(({ id, raised }) => ({ id, ratio: contrastRatio(token, raised) }))
      .filter(({ ratio }) => ratio < AA_CONTRAST)
      .map(({ id, ratio }) => `${id} ${ratio.toFixed(2)}:1`);
    assert.deepEqual(failures, [], `${token} is under ${AA_CONTRAST}:1 on:\n` + failures.join("\n"));
  });

  test(`--cf-${role} reads on every dark theme's raised surface`, () => {
    const token = statusToken(css, role).dark;
    const failures = themes.dark
      .map(({ id, raised }) => ({ id, ratio: contrastRatio(token, raised) }))
      .filter(({ ratio }) => ratio < AA_CONTRAST)
      .map(({ id, ratio }) => `${id} ${ratio.toFixed(2)}:1`);
    assert.deepEqual(failures, [], `${token} is under ${AA_CONTRAST}:1 on:\n` + failures.join("\n"));
  });
}
