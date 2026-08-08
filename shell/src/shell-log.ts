import { appendFileSync, mkdirSync, renameSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

/**
 * What the Electron main process said, on disk.
 *
 * The sidecar has had `Diagnostics/ErrorLog` since the log directory was found to be empty on every
 * launch; this half never got one. Everything this process knows about a failed startup — the core's
 * stderr, the exit code, "the CodeFlow core is unavailable" — went to `console.error`, and a packaged
 * build has no console attached to it. On Windows that is the whole diagnostic trail for an app that
 * starts, shows its window, and then answers nothing: nowhere, by construction.
 *
 * Same shape as the sidecar's log deliberately, so the two files read alike and land side by side:
 * one line per event, appended and flushed, one rollover rather than a rotation scheme, **never
 * throws**, and credentials redacted before anything is written.
 */

/** Bytes kept before the file is rolled over. One `.1` sibling, not an archive. */
const MAX_BYTES = 2 * 1024 * 1024;

/**
 * The same base directory the sidecar resolves in `Platform/AppPaths.cs`.
 *
 * Duplicated rather than asked for, because the process that needs to write this line is often the
 * one that could not reach the sidecar at all. `C:\CodeFlow` is `DIVERGENCE-BOOT-a` — a deliberate
 * literal the NSIS uninstaller hardcodes a third time — so this is the second copy becoming a third,
 * and the two must be kept in step by hand.
 */
export function baseDirectory(): string {
  if (process.platform === "win32") return "C:\\CodeFlow";
  const home = homedir();
  return join(home === "" ? "." : home, "CodeFlow");
}

export function logsDirectory(): string {
  return join(baseDirectory(), "logs");
}

/** The log file inside a given directory. */
export function shellLogFile(directory: string): string {
  return join(directory, "shell.log");
}

/** A URL carrying `user:password`, as git prints it back on a failed exchange. */
const CREDENTIAL_IN_URL = /(\w+):\/\/[^/\s:@]+:[^/\s@]+@/g;

/**
 * An auth header echoed inside an error body, scheme and value together.
 *
 * The value stops at the first quote, comma or brace rather than the first space: these arrive
 * embedded in JSON, and a greedy run of non-space would swallow the syntax around it. The header
 * name survives — knowing a request carried an `Authorization` at all is part of the diagnostic.
 */
const AUTH_HEADER = /\b(authorization|x-api-key|private-token|api-key)\s*:\s*(?:bearer\s+|token\s+|basic\s+)?[^\s"',}\]]+/gi;

/** A `Bearer …` with no header name in front of it. */
const BARE_BEARER = /\bbearer\s+[A-Za-z0-9._-]{8,}/gi;

/** A token recognisable on its own, by the prefixes the hosts publish. */
const TOKEN_LITERAL =
  /\b(gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|xox[abposr]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9-]{20,})/g;

/**
 * Blanks out anything in a message that looks like a credential.
 *
 * The `[core] …` lines this file carries are the sidecar's own stderr, and a failed `fetch` prints
 * the remote URL it tried — which in a great many repositories carries an embedded token. None of
 * that mattered while the text died in a discarded console; it matters now that it is a file whose
 * whole purpose is to be sent to somebody else.
 *
 * Deliberately blunt, matching `ErrorLog.Redact`: it replaces rather than detects. A false positive
 * costs a line of diagnostics, a false negative costs a credential.
 */
export function redact(message: string): string {
  if (message === "") return message;

  return message
    .replace(CREDENTIAL_IN_URL, "$1://***:***@")
    .replace(AUTH_HEADER, "$1: ***")
    .replace(BARE_BEARER, "Bearer ***")
    .replace(TOKEN_LITERAL, "***");
}

export type ShellLogLevel = "info" | "warn" | "error";

/** Formats one line. Exported for the test, which should not have to parse a timestamp. */
export function formatLine(timestamp: string, level: ShellLogLevel, message: string): string {
  return `${timestamp}  ${level.toUpperCase().padEnd(5)}  ${redact(message)}`;
}

/**
 * Appends one line to a named directory's log.
 *
 * The directory is a parameter for the same reason the sidecar's is: without it the test suite
 * writes its fixtures into the user's real `~/CodeFlow/logs`.
 *
 * Synchronous on purpose — the most important thing this ever records is the last thing the process
 * does before it dies, and an async write would not survive that.
 */
export function recordIn(directory: string, level: ShellLogLevel, message: string): void {
  try {
    mkdirSync(directory, { recursive: true });
    const path = shellLogFile(directory);

    try {
      if (statSync(path).size > MAX_BYTES) renameSync(path, `${path}.1`);
    } catch {
      // No file yet, or one that cannot be stat'd. Either way there is nothing to roll over.
    }

    const line = formatLine(new Date().toISOString(), level, message);
    appendFileSync(path, `${line}\n`, "utf8");
  } catch {
    // A full disk, a read-only directory, a path the OS refuses. None of them is a reason to fail
    // the thing this was only recording — least of all a startup that is already failing.
  }
}

/**
 * Records one line, and mirrors it to the console.
 *
 * Both, not either: the console is what a developer running `pnpm -C shell dev` reads, and the file
 * is the only thing that exists in a packaged build.
 */
export function record(level: ShellLogLevel, message: string): void {
  const sink = level === "error" ? console.error : level === "warn" ? console.warn : console.log;
  sink(message);
  recordIn(logsDirectory(), level, message);
}
