import { describe, expect, test } from "vitest";
import { EDITOR_RESERVED, reservedBy, reservedChordFor } from "./shortcuts";

/**
 * `EDITOR_RESERVED` is read from both ends — settings warns when a user binds one of these chords,
 * and the editor's activity rail shows the chord in its tooltip. The rail used to hardcode the
 * chord in the tooltip string, so a rebind here left it lying with nothing failing.
 */
describe("editor-reserved chords", () => {
  test("the two lookups are inverses on every entry", () => {
    for (const { chord, labelKey } of EDITOR_RESERVED) {
      expect(reservedBy(chord)).toBe(labelKey);
      // Not `toBe(chord)`: two entries share a label (Alt+Up / Alt+Down both move a line), so the
      // reverse lookup answers with the first — which must still be a chord that maps back.
      expect(reservedBy(reservedChordFor(labelKey)!)).toBe(labelKey);
    }
  });

  test("an action the editor does not reserve has no chord", () => {
    expect(reservedChordFor("common.close")).toBeNull();
    expect(reservedBy("Mod+Shift+Alt+Q")).toBeNull();
  });
});
