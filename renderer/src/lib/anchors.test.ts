import { describe, expect, test } from "vitest";
import { ANCHOR_TAGS, anchorPatternSource, parseAnchors } from "./anchors";

/**
 * The tagged-comment scanner, and the pattern it shares with the sidecar.
 *
 * `anchorPatternSource` is a cross-language contract with a shape nothing was checking. The panel's
 * project-wide scan sends this exact string to `search_repo`, which runs it through .NET's `Regex`;
 * the hits that come back are then re-parsed here to pull out the tag and the message. If the two
 * engines disagree about the pattern, the panel lists files the second pass then finds nothing in —
 * an empty panel with no error, which is the failure the file's own comment warns about.
 *
 * These tests cannot run .NET, so they pin the two things that would break it: the exact source
 * string, and the promise that it stays inside the syntax both engines accept.
 */

describe("the shared pattern", () => {
  test("is exactly this string", () => {
    // Deliberately a literal. Changing the pattern is allowed; changing it *silently* is not — this
    // fails, and whoever updates it has to confirm .NET still accepts it and that `search_repo`
    // returns the same hits.
    expect(anchorPatternSource(["TODO", "FIXME"])).toBe(
      String.raw`(?://+|/\*+|\*+|#+|--+|<!--+|;+|%+|"""|''')\s*(TODO|FIXME)\b[:\-]?[ \t]*`,
    );
  });

  test("uses no syntax the non-backtracking engine rejects", () => {
    const source = anchorPatternSource();

    // Lookaround and backreferences are the two things a non-backtracking engine rejects outright.
    // The sidecar compiles this pattern with RegexOptions.NonBacktracking, so a pattern using
    // either would work in the editor and fail in the backend.
    expect(source).not.toMatch(/\(\?[=!<]/);
    expect(source).not.toMatch(/\\[1-9]/);
  });

  test("covers every declared tag by default", () => {
    const source = anchorPatternSource();

    for (const tag of ANCHOR_TAGS) {
      expect(source).toContain(tag.id);
    }
  });
});

describe("finding anchors in a file", () => {
  const find = (text: string) => parseAnchors(text);

  test("reads the tag, the 1-based line and the message", () => {
    const [anchor] = find("const x = 1;\n// TODO: extraer esto\n");
    if (!anchor) throw new Error("expected an anchor");

    expect(anchor).toMatchObject({ tag: "TODO", line: 2, text: "extraer esto" });
    // 1-based column of the tag itself, which is where the editor puts the caret on a jump.
    expect(anchor.column).toBe(4);
  });

  test("every comment style this editor opens is recognised", () => {
    const lines = [
      "// TODO: c-family",
      "# FIXME: shell",
      "-- NOTE: sql",
      "<!-- HACK: html -->",
      "; TODO: ini",
      "% FIXME: latex",
      '""" NOTE: docstring',
      " * TODO: block continuation",
    ].join("\n");

    expect(find(lines)).toHaveLength(8);
  });

  test("a word merely starting with a tag is not an anchor", () => {
    // The `\b` after the tag. Without it every `// TODOS` line would join the panel.
    expect(find("// TODOS: plural")).toHaveLength(0);
  });

  test("a bare marker with no separator still counts, with empty text", () => {
    const [anchor] = find("// ANCHOR");

    expect(anchor).toMatchObject({ tag: "ANCHOR", text: "" });
  });

  test("two anchors on one line do not swallow each other", () => {
    const found = find("// TODO: primero // NOTE: segundo");

    expect(found.map((a) => [a.tag, a.text])).toEqual([
      ["TODO", "primero"],
      ["NOTE", "segundo"],
    ]);
  });

  test("a trailing comment close is not part of the note", () => {
    const [blockComment] = find("/* TODO: cerrar esto */");
    const [htmlComment] = find("<!-- FIXME: y esto -->");
    if (!blockComment || !htmlComment) throw new Error("expected an anchor");

    expect(blockComment.text).toBe("cerrar esto");
    expect(htmlComment.text).toBe("y esto");
  });

  test("a file with nothing tagged yields nothing", () => {
    expect(find("const x = 1;\n// just a comment\n")).toEqual([]);
  });
});
