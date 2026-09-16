import { describe, expect, it } from "vitest";
import { DB_CONNECTION_REFUSED, connectionRefusal } from "./connectionError";

describe("connectionRefusal", () => {
  it("returns the driver's own sentence, without the marker", () => {
    const error = new Error(`${DB_CONNECTION_REFUSED}password authentication failed for user "gaston"`);

    expect(connectionRefusal(error)).toBe('password authentication failed for user "gaston"');
  });

  it("finds the marker after the transport's own wrapping", () => {
    // The shell and Electron each add a prefix on the way up; matching by index rather than by
    // `startsWith` is what survives that.
    const error = `Error: ${DB_CONNECTION_REFUSED}Connection refused (localhost:5432)`;

    expect(connectionRefusal(error)).toBe("Connection refused (localhost:5432)");
  });

  it("answers null for a failure that is not a refusal", () => {
    // "The database said no" and "the command blew up" lead to different next steps: a wrong port
    // against a bug.
    expect(connectionRefusal(new Error("unknown command 'dbml_introspect_database'"))).toBeNull();
  });

  it("answers null rather than throwing on a non-error", () => {
    expect(connectionRefusal(undefined)).toBeNull();
    expect(connectionRefusal(null)).toBeNull();
  });
});
