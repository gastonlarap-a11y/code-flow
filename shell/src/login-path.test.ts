import assert from "node:assert/strict";
import { test } from "node:test";
import { extractPath, mergePath } from "./login-path";

// The shell has no test runner of its own and gains none here: `node --test` ships with Node.
// What is covered is the parsing and the merge — the two places where a wrong answer is silent.
// Spawning a real login shell is not covered, because its result depends on the machine's profile
// and a test that passes only on this laptop is worse than no test.

const MARKER = "__CODEFLOW_ENV__";

test("reads PATH out of a marked env block", () => {
  const output = [MARKER, "SHELL=/bin/zsh", "PATH=/a:/b", "TERM=xterm", MARKER].join("\n");

  assert.equal(extractPath(output), "/a:/b");
});

test("ignores everything a chatty profile prints around the block", () => {
  // This is the case the markers exist for: mise, oh-my-zsh and a motd all print on startup, and
  // without delimiters the first line of a banner is as plausible an answer as the real one.
  const output = [
    "mise: installing node@24…",
    "Last login: Wed Jul 30 11:02:14 on ttys004",
    MARKER,
    "PATH=/opt/homebrew/bin:/usr/bin",
    MARKER,
    "you have mail",
  ].join("\n");

  assert.equal(extractPath(output), "/opt/homebrew/bin:/usr/bin");
});

test("a PATH containing an equals sign survives", () => {
  // `env` output is split on the first `=` only. A directory with an `=` in its name is legal.
  const output = [MARKER, "PATH=/a:/weird=dir:/b", MARKER].join("\n");

  assert.equal(extractPath(output), "/a:/weird=dir:/b");
});

test("no marker, no answer", () => {
  assert.equal(extractPath("PATH=/a:/b"), null);
});

test("one marker is not a block", () => {
  // A shell killed by the timeout mid-write leaves exactly this, and it must not be parsed.
  assert.equal(extractPath(`${MARKER}\nPATH=/a:/b`), null);
});

test("a block without PATH yields nothing rather than an empty PATH", () => {
  assert.equal(extractPath([MARKER, "SHELL=/bin/zsh", MARKER].join("\n")), null);
});

test("an empty PATH is not an answer", () => {
  assert.equal(extractPath([MARKER, "PATH=", MARKER].join("\n")), null);
});

test("captured entries come first and inherited ones are kept", () => {
  assert.equal(mergePath("/opt/homebrew/bin:/usr/bin", "/usr/bin:/bin"), "/opt/homebrew/bin:/usr/bin:/bin");
});

test("a duplicate keeps its earliest position", () => {
  // Order is the whole point: a version manager's shim has to win over a system binary of the same
  // name, which is what the user's terminal does.
  assert.equal(mergePath("/a:/b:/a", "/b:/c"), "/a:/b:/c");
});

test("empty entries are dropped", () => {
  // A trailing colon means "the current directory" to some shells; carrying it forward would put
  // whatever directory the app happens to be in onto the search path.
  assert.equal(mergePath("/a::/b:", "::/c"), "/a:/b:/c");
});

test("no inherited PATH is not an error", () => {
  assert.equal(mergePath("/a:/b", undefined), "/a:/b");
});
