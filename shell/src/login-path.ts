import { spawn } from "node:child_process";

/**
 * Recovers the `PATH` a terminal would have, for an app launched from Finder.
 *
 * A macOS app opened from Finder, the Dock or a `.dmg` inherits launchd's environment, and on this
 * machine `launchctl getenv PATH` is empty — so the app gets the bare
 * `/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin` and nothing else. No Homebrew, no `~/.local/bin`,
 * and in particular nothing a Node version manager (mise, nvm, asdf, volta, fnm) put there. The
 * user's shell profile, which is what puts those directories on `PATH`, is never read.
 *
 * `BinaryDiscovery.InstallDirs()` in the sidecar covers the fixed set of directories the AI CLI
 * installers are known to use, and that is what 1.7.2 covered. But it cannot cover a
 * version manager, whose directories carry a version number nobody can guess. CodeFlow 1.7.2 has the
 * same blind spot; it just never shows, because a developer runs a plain dev server from a terminal that
 * did read the profile.
 *
 * So this asks the login shell directly, once, before the sidecar starts. Everything downstream
 * inherits the result: the AI engines, `npx` for skill installs, `code` for "open in VS Code", and
 * `gh`.
 */

/** Wraps the shell's output so a chatty profile cannot be mistaken for the answer. */
const MARKER = "__CODEFLOW_ENV__";

/**
 * How long the login shell gets before it is killed and the inherited `PATH` is kept.
 *
 * An interactive shell is asked for here (`-i`), because that is the only mode in which `.zshrc` —
 * where mise, nvm and friends install themselves — is read. The cost is that an interactive shell
 * can block on something a person would answer, and a login shell that never returns would
 * otherwise mean an app that never opens. Two seconds is far past a healthy profile and far short
 * of a user noticing.
 */
const TIMEOUT_MS = 2_000;

/**
 * Pulls `PATH` out of the marked block of a login shell's output.
 *
 * The shell is asked to run `env` rather than to echo `$PATH`, which is what makes this work in any
 * shell rather than only in POSIX ones: `env` prints the real exported environment, while `$PATH`
 * is a list in fish and would come back space-joined and useless. The block is delimited because a
 * profile that prints a banner, a version-manager notice or a motd would otherwise be parsed as
 * part of the answer.
 *
 * Exported for the tests.
 */
export function extractPath(output: string): string | null {
  const start = output.indexOf(MARKER);
  if (start < 0) return null;

  const end = output.indexOf(MARKER, start + MARKER.length);
  if (end < 0) return null;

  for (const line of output.slice(start + MARKER.length, end).split("\n")) {
    if (line.startsWith("PATH=")) {
      const value = line.slice("PATH=".length).trim();
      return value.length > 0 ? value : null;
    }
  }

  return null;
}

/**
 * Puts the captured entries first and keeps the inherited ones after, without duplicates.
 *
 * Merging rather than replacing matters: Electron's own `PATH` can hold entries the login shell
 * knows nothing about, and dropping them would trade one class of "command not found" for another.
 * Captured entries go first because they are the more specific answer — a version manager's shim
 * should win over a system-wide binary of the same name, which is exactly what the user's terminal
 * does.
 *
 * Exported for the tests.
 */
export function mergePath(captured: string, inherited: string | undefined): string {
  const seen = new Set<string>();
  const merged: string[] = [];

  for (const entry of [...captured.split(":"), ...(inherited ?? "").split(":")]) {
    if (entry.length === 0 || seen.has(entry)) continue;
    seen.add(entry);
    merged.push(entry);
  }

  return merged.join(":");
}

/** Asks the login shell for its environment, or resolves null if it cannot be had. */
function readLoginShellEnvironment(): Promise<string | null> {
  return new Promise((resolve) => {
    // `/bin/sh` rather than a guess at the user's shell: if SHELL is unset there is nothing to
    // infer, and every Unix has `sh`.
    const shell = process.env.SHELL ?? "/bin/sh";

    let child;
    try {
      child = spawn(shell, ["-ilc", `echo ${MARKER}; env; echo ${MARKER}`], {
        // stderr is discarded on purpose: an interactive shell writes prompts, job-control notices
        // and deprecation warnings there, none of which are an error for this.
        stdio: ["ignore", "pipe", "ignore"],
      });
    } catch {
      // A SHELL pointing at something unspawnable is the user's business, not a reason to fail.
      resolve(null);
      return;
    }

    let settled = false;
    let output = "";

    const finish = (value: string | null) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(value);
    };

    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      finish(null);
    }, TIMEOUT_MS);
    // Node keeps the process alive for a pending timer; this one must not delay a quit.
    timer.unref?.();

    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      output += chunk;
    });

    child.on("error", () => finish(null));
    child.on("close", () => finish(output));
  });
}

/**
 * Widens this process's `PATH` with the login shell's, so children inherit it.
 *
 * Mutating `process.env` is the point rather than a shortcut: `startSidecar` spawns without an
 * `env` option, so the sidecar inherits `process.env` as it stands when it starts — which is why
 * this has to be awaited before it, and why nothing else has to change.
 *
 * Never throws and never rejects. A failure here degrades to the `PATH` the app already had, which
 * is exactly today's behaviour.
 */
export async function applyLoginShellPath(): Promise<void> {
  // Windows has no login shell to read and no launchd to lose the environment to: a process there
  // already carries the user's `PATH`. (It can be stale if a CLI was installed while the app ran,
  // which `InstallDirs()` in the sidecar covers.)
  if (process.platform === "win32") return;

  const captured = extractPath((await readLoginShellEnvironment()) ?? "");
  if (captured === null) return;

  process.env.PATH = mergePath(captured, process.env.PATH);
}
