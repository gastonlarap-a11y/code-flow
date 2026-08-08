import { describe, expect, test } from "vitest";
import { unwrapInvokeError } from "./host";

/**
 * The transport's own noise, removed at the boundary that adds it.
 *
 * `ipcMain.handle` re-throws every rejection as `Error invoking remote method '<channel>': <real
 * message>`. The AI panel renders a failed run's message verbatim, so that prefix was reaching the
 * screen — a user analysing a clean working tree read `codeflow:invoke` as the explanation.
 *
 * The sentinel prefixes the renderer parses (`STALE_REVIEW: `, `QUOTA_EXCEEDED::`, …) sit *inside*
 * the real message, so they must survive this untouched — stripping one would silently turn a
 * handled state back into a plain error.
 */
describe("unwrapInvokeError", () => {
  test("removes Electron's channel wrapper", () => {
    expect(unwrapInvokeError("Error invoking remote method 'codeflow:invoke': the tree is clean")).toBe(
      "the tree is clean",
    );
  });

  test("removes it whatever the channel is called", () => {
    expect(unwrapInvokeError("Error invoking remote method 'codeflow:something-else': boom")).toBe("boom");
  });

  test("leaves a message that never had the wrapper alone", () => {
    expect(unwrapInvokeError("the CodeFlow shell bridge is unavailable")).toBe(
      "the CodeFlow shell bridge is unavailable",
    );
  });

  test("keeps the sentinel prefixes the renderer parses", () => {
    for (const sentinel of [
      "STALE_REVIEW: reviewed abc1234, head is now def5678",
      "CREDENTIAL_REFUSED: the token was rejected",
      "SELF_APPROVAL: you opened this pull request",
      "CHECKOUT_CONFLICT: uncommitted changes",
      "NOTHING_TO_ANALYZE: the working tree is clean",
    ]) {
      expect(unwrapInvokeError(`Error invoking remote method 'codeflow:invoke': ${sentinel}`)).toBe(sentinel);
    }
  });

  test("only strips a leading wrapper, never one quoted inside a message", () => {
    const quoted = "the log said: Error invoking remote method 'codeflow:invoke': nested";
    expect(unwrapInvokeError(quoted)).toBe(quoted);
  });

  test("drops the bare Error: Electron leaves inside its own wrapper", () => {
    // What a Windows user was actually shown: "Something failed unexpectedly: Error: Error: the
    // CodeFlow core is not running". Electron serialises the rejection with `toString`, so the inner
    // `Error: ` is the transport's, not the sidecar's, and `String(error)` downstream adds a third.
    expect(
      unwrapInvokeError("Error invoking remote method 'codeflow:invoke': Error: the CodeFlow core is not running"),
    ).toBe("the CodeFlow core is not running");
  });

  test("a message that only looks like it was wrapped keeps its own words", () => {
    // No wrapper means nothing to correct for: this text is the sidecar's own, and trimming a
    // leading `Error:` out of it would be editing the message rather than the transport.
    expect(unwrapInvokeError("Error: git said no")).toBe("Error: git said no");
  });

  test("a sentinel still survives the second strip", () => {
    expect(
      unwrapInvokeError("Error invoking remote method 'codeflow:invoke': Error: STALE_REVIEW: head moved"),
    ).toBe("STALE_REVIEW: head moved");
  });
});
