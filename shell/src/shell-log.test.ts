import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { formatLine, recordIn, redact, shellLogFile } from "./shell-log";

// What is covered is redaction and rollover — the two places where a wrong answer is silent. A
// redaction that misses leaks a token into a file whose purpose is to be sent to somebody else, and
// a rollover that never fires turns a log nobody looks at into a disk problem.

const TIMESTAMP = "2026-08-06T12:00:00.000Z";

test("a URL carrying user:password loses both halves", () => {
  const line = redact("failed to fetch https://gaston:ghp_secretvalue@github.com/acme/app.git");

  assert.equal(line, "failed to fetch https://***:***@github.com/acme/app.git");
});

test("a bare auth header keeps its name and loses its value", () => {
  // The name is what makes the line diagnostic: knowing the request carried an Authorization at all
  // is the difference between "no credential was sent" and "the one we sent was refused".
  assert.equal(redact("X-API-Key: sk-0123456789abcdef0123"), "X-API-Key: ***");
  assert.equal(redact("Private-Token: abcdef0123456789"), "Private-Token: ***");
});

test("a header quoted inside a JSON body is caught by the value rules instead", () => {
  // The header rule cannot fire here: its value pattern excludes the quote that follows the colon,
  // so what saves this line is the bare-Bearer and token-prefix rules behind it. Pinned because the
  // three overlap on purpose — one of them missing is only visible in a case like this one.
  assert.equal(redact('{"Authorization": "Bearer abc123def456"}'), '{"Authorization": "Bearer ***"}');
  assert.equal(
    redact('{"authorization":"token ghp_0123456789abcdef0123","accept":"application/json"}'),
    '{"authorization":"token ***","accept":"application/json"}',
  );
});

test("a token with a published prefix goes even with no header in front of it", () => {
  assert.equal(
    redact("remote rejected: github_pat_11ABCDEFG0123456789_abcdefgh is not authorized"),
    "remote rejected: *** is not authorized",
  );
});

test("a bare Bearer goes too", () => {
  assert.equal(redact("retrying with Bearer eyJhbGciOiJIUzI1NiJ9"), "retrying with Bearer ***");
});

test("an ordinary message survives intact", () => {
  // The blunt rules cost a line of diagnostics when they fire wrongly, so the common case is worth
  // pinning: a startup failure names a path and an exit code, and neither may be mangled.
  const message = "the CodeFlow core was not found at C:\\Program Files\\CodeFlow\\core\\codeflow-core.exe";

  assert.equal(redact(message), message);
});

test("a line carries its timestamp and level", () => {
  assert.equal(formatLine(TIMESTAMP, "error", "it exited with code 134"), `${TIMESTAMP}  ERROR  it exited with code 134`);
  assert.equal(formatLine(TIMESTAMP, "info", "ready"), `${TIMESTAMP}  INFO   ready`);
});

test("a line is redacted on its way into the file, not merely on the way out", () => {
  const directory = mkdtempSync(join(tmpdir(), "codeflow-shell-log-"));

  recordIn(directory, "error", "clone failed: https://u:ghp_abcdef0123456789@github.com/a/b.git");

  const written = readFileSync(shellLogFile(directory), "utf8");
  assert.ok(written.includes("https://***:***@github.com/a/b.git"), written);
  assert.ok(!written.includes("ghp_abcdef0123456789"), "the token reached the file");
});

test("the file rolls over once it passes its ceiling", () => {
  const directory = mkdtempSync(join(tmpdir(), "codeflow-shell-log-"));
  const path = shellLogFile(directory);
  writeFileSync(path, "x".repeat(2 * 1024 * 1024 + 1), "utf8");

  recordIn(directory, "info", "after the rollover");

  assert.ok(statSync(`${path}.1`).size > 2 * 1024 * 1024, "the old file was not kept as .1");
  assert.equal(readFileSync(path, "utf8").trimEnd().endsWith("after the rollover"), true);
});

test("an unwritable directory is not a reason to throw", () => {
  // The most important line this ever writes is the last thing the process does before it dies.
  // Failing there would replace a diagnosable crash with an undiagnosable one.
  assert.doesNotThrow(() => recordIn(join("\0invalid"), "error", "boom"));
});
