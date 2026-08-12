import { describe, expect, test } from "vitest";
import { nextProjectColor, PROJECT_COLORS } from "./projectColor";

describe("nextProjectColor", () => {
  test("the first repository takes the first colour", () => {
    expect(nextProjectColor([])).toBe(PROJECT_COLORS[0]);
  });

  test("repositories added one after another are all different", () => {
    // The whole point: eight repositories, eight colours, without anyone opening Settings.
    const chosen: string[] = [];
    for (let i = 0; i < PROJECT_COLORS.length; i++) chosen.push(nextProjectColor(chosen));

    expect(new Set(chosen).size).toBe(PROJECT_COLORS.length);
  });

  test("past the palette it starts a second round rather than giving up", () => {
    const nine = [...PROJECT_COLORS];
    expect(nine).toContain(nextProjectColor(nine));
  });

  test("a colour freed by a removed repository is handed out again", () => {
    // What a counter would get wrong: it would advance past the free hue and repeat a used one.
    const all = [...PROJECT_COLORS];
    const freed = all[3]!;

    expect(nextProjectColor(all.filter((c) => c !== freed))).toBe(freed);
  });

  test("the least used wins even when nothing has been freed", () => {
    // Two of the first colour, one of the second, none of the third.
    const existing = [PROJECT_COLORS[0]!, PROJECT_COLORS[0]!, PROJECT_COLORS[1]!];

    expect(nextProjectColor(existing)).toBe(PROJECT_COLORS[2]);
  });

  test("a colour is recognised whatever case it was stored in", () => {
    const shouted = PROJECT_COLORS.map((c) => c.toUpperCase());

    // Uppercase hex is the same colour; counting it as unknown would hand it straight back out and
    // put two identical repositories side by side.
    expect(nextProjectColor(shouted.slice(0, 1))).not.toBe(PROJECT_COLORS[0]);
  });

  test("a colour from outside the palette is ignored rather than returned", () => {
    // Someone's hand-picked hue, or one from an older palette. It cannot be handed out from here.
    expect(PROJECT_COLORS).toContain(nextProjectColor(["#123456", "#abcdef"]));
  });
});
