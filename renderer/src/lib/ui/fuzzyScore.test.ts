import { describe, expect, test } from "vitest";
import { fuzzyScore, rankByFuzzy } from "./fuzzyScore";

describe("fuzzyScore", () => {
  test("an empty query matches everything equally", () => {
    expect(fuzzyScore("src/App.tsx", "")).toBe(0);
  });

  test("returns null when the characters are not all there, in order", () => {
    expect(fuzzyScore("src/App.tsx", "zzz")).toBeNull();
    // Present, but not in this order — a subsequence is ordered by definition.
    expect(fuzzyScore("abc", "cba")).toBeNull();
  });

  test("a hit in the filename outranks a hit in the directory", () => {
    // "editor" appears in both, and the file called `Editor` is the one meant.
    const inName = fuzzyScore("src/git/Editor.tsx", "editor");
    const inDir = fuzzyScore("src/editor/Panel.tsx", "editor");
    expect(inName).not.toBeNull();
    expect(inDir).not.toBeNull();
    expect(inName!).toBeLessThan(inDir!);
  });

  test("a substring outranks a subsequence", () => {
    const substring = fuzzyScore("EditorView.tsx", "view");
    const subsequence = fuzzyScore("EditorView.tsx", "edvw");
    expect(substring!).toBeLessThan(subsequence!);
  });

  test("finds a spread-out subsequence, the quick-open case", () => {
    expect(fuzzyScore("EditorView.tsx", "edvw")).not.toBeNull();
  });

  test("an earlier match scores better than a later one", () => {
    expect(fuzzyScore("app.ts", "app")!).toBeLessThan(fuzzyScore("myapp.ts", "app")!);
  });

  test("a wider-spread subsequence scores worse", () => {
    const tight = fuzzyScore("ab.ts", "ab")!;
    const spread = fuzzyScore("axxxxb.ts", "ab")!;
    expect(tight).toBeLessThan(spread);
  });
});

describe("rankByFuzzy", () => {
  const files = ["src/state/uiStore.ts", "src/lib/ui/icons.ts", "src/components/common/Button.tsx"];

  test("orders by score and honours the limit", () => {
    expect(rankByFuzzy(files, "uistore", (f) => f, 10)).toEqual(["src/state/uiStore.ts"]);
    expect(rankByFuzzy(files, "", (f) => f, 2)).toHaveLength(2);
  });

  test("drops what does not match at all", () => {
    expect(rankByFuzzy(files, "qqqq", (f) => f, 10)).toEqual([]);
  });

  test("breaks ties on length, so the more specific path wins", () => {
    // Both match "a" in the filename at index 0; the shorter one is the tighter answer.
    const ranked = rankByFuzzy(["a.ts", "aaaaaaa.ts"], "a", (f) => f, 10);
    expect(ranked[0]).toBe("a.ts");
  });

  test("trims and lowercases the query once, for the caller", () => {
    expect(rankByFuzzy(files, "  UISTORE  ", (f) => f, 10)).toEqual(["src/state/uiStore.ts"]);
  });
});
