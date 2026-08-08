import { describe, expect, it } from "vitest";
import { runStatSegments } from "./runStats";

describe("runStatSegments", () => {
  it("splits a real footer into the facts it carries", () => {
    const footer =
      "🤖 Análisis automatizado (pr-review) · Claude Code (claude-sonnet-5) · 2026-08-03 02:33 · " +
      "512.849 tokens (6.389.506 desde caché) · equiv. API USD 1,2345 · nivel completo · 6 min 23 s · " +
      "diff: 12 de 14 archivos, 2 excluidos · 10 hallazgos: 3 nuevos, 4 persisten, 3 resueltos";

    expect(runStatSegments(footer)).toEqual([
      "Análisis automatizado (pr-review)",
      "Claude Code (claude-sonnet-5)",
      "2026-08-03 02:33",
      "512.849 tokens (6.389.506 desde caché)",
      "equiv. API USD 1,2345",
      "nivel completo",
      "6 min 23 s",
      "diff: 12 de 14 archivos, 2 excluidos",
      "10 hallazgos: 3 nuevos, 4 persisten, 3 resueltos",
    ]);
  });

  // A run on an engine that reports no usage stamps the same line minus the spend segments, and a
  // link review stamps it minus the coverage. Neither is a special case here.
  it("keeps a short footer whole", () => {
    expect(runStatSegments("🤖 Análisis automatizado (pr-review) · Claude Code (modelo predeterminado)")).toEqual([
      "Análisis automatizado (pr-review)",
      "Claude Code (modelo predeterminado)",
    ]);
  });

  it("has nothing to show when nothing was stamped", () => {
    expect(runStatSegments(null)).toEqual([]);
    expect(runStatSegments("")).toEqual([]);
    expect(runStatSegments("   ")).toEqual([]);
  });
});
