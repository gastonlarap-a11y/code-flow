import { describe, expect, it } from "vitest";
import { documentName, normalizeDocumentPath } from "./documentPath";

/** The happy path unwrapped, so a case that unexpectedly fails reports its reason. */
function pathOf(input: string): string {
  const result = normalizeDocumentPath(input);
  if (!result.ok) throw new Error(`expected a path, got reason "${result.reason}"`);
  return result.relPath;
}

describe("normalizeDocumentPath", () => {
  it("appends the extension when it is missing", () => {
    // The sidecar's walk matches on `.dbml`, so a document saved without it would vanish from the
    // picker the next time it reloaded.
    expect(pathOf("orders")).toBe("orders.dbml");
  });

  it("leaves an extension already there, whatever case it was typed in", () => {
    expect(pathOf("orders.dbml")).toBe("orders.dbml");
    expect(pathOf("orders.DBML")).toBe("orders.DBML");
  });

  it("normalises backslashes, so one document is one layout key", () => {
    expect(pathOf("db\\schema\\orders")).toBe("db/schema/orders.dbml");
  });

  it("collapses repeated and trailing separators", () => {
    expect(pathOf("db//orders/")).toBe("db/orders.dbml");
  });

  it("trims what the user typed", () => {
    expect(pathOf("  orders  ")).toBe("orders.dbml");
  });

  it("refuses a name that would leave the project folder", () => {
    // Refused here rather than only at the boundary: `create_file` guards its own path, but its
    // refusal reaches the user as a raw error string instead of a labelled field.
    for (const input of ["../outside", "db/../../outside", ".."]) {
      expect(normalizeDocumentPath(input)).toEqual({ ok: false, reason: "escapes" });
    }
  });

  it("refuses an absolute path", () => {
    // A lone "/" is reported as absolute rather than empty: that is what the user typed, and it is
    // the more useful of the two things to be told.
    for (const input of ["/etc/schema", "/"]) {
      expect(normalizeDocumentPath(input)).toEqual({ ok: false, reason: "absolute" });
    }
  });

  it("refuses characters a file name cannot hold", () => {
    // `:` is in here, which is also what rejects a Windows drive letter like `C:/schema`.
    for (const input of ["a<b", "a>b", 'a"b', "a|b", "a?b", "a*b", "C:/schema"]) {
      expect(normalizeDocumentPath(input)).toEqual({ ok: false, reason: "invalidChar" });
    }
  });

  it("refuses control characters, which no file name holds", () => {
    // Checked by code point rather than in the pattern, so this is its own branch to cover.
    for (const input of [`a${String.fromCharCode(0)}b`, `a${String.fromCharCode(0x1f)}b`, `a${String.fromCharCode(0x7f)}b`]) {
      expect(normalizeDocumentPath(input)).toEqual({ ok: false, reason: "invalidChar" });
    }
  });

  it("refuses an empty name, including one that is only the extension", () => {
    for (const input of ["", "   ", ".dbml"]) {
      expect(normalizeDocumentPath(input)).toEqual({ ok: false, reason: "empty" });
    }
  });

  it("refuses a bare dot rather than reading it as the folder itself", () => {
    expect(normalizeDocumentPath(".")).toEqual({ ok: false, reason: "escapes" });
  });
});

describe("documentName", () => {
  it("is the last segment", () => {
    expect(documentName("db/nested/orders.dbml")).toBe("orders.dbml");
    expect(documentName("orders.dbml")).toBe("orders.dbml");
  });
});
