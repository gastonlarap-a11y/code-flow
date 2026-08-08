import assert from "node:assert/strict";
import { join } from "node:path";
import { test } from "node:test";
import { CONTENT_SECURITY_POLICY, contentTypeFor, isWithinRoot } from "./app-protocol";

// These two functions decide what the `app://` handler serves and under what policy. Both fail
// quietly when they are wrong — a containment hole serves a file nobody meant to expose, a missing
// charset renders accented text as mojibake — so both are pinned here.

const ROOT = join("/", "app", "renderer");

test("a file inside the root is served", () => {
  assert.equal(isWithinRoot(ROOT, join(ROOT, "index.html")), true);
  assert.equal(isWithinRoot(ROOT, join(ROOT, "assets", "index.js")), true);
});

test("the root itself is inside itself", () => {
  // A request for the directory is not an escape attempt, and must not be treated as one.
  assert.equal(isWithinRoot(ROOT, ROOT), true);
});

test("a sibling that merely shares the prefix is refused", () => {
  // The whole reason this is a function: `startsWith` accepts this, because the prefix matches
  // without a separator behind it.
  assert.equal(isWithinRoot(ROOT, join("/", "app", "renderer-evil", "secret")), false);
  assert.equal(isWithinRoot(ROOT, join("/", "app", "renderer.bak")), false);
});

test("a traversal out of the root is refused", () => {
  assert.equal(isWithinRoot(ROOT, join("/", "app", "secret")), false);
  assert.equal(isWithinRoot(ROOT, join("/", "etc", "passwd")), false);
});

test("the parent directory is refused", () => {
  assert.equal(isWithinRoot(ROOT, join("/", "app")), false);
});

test("text types declare utf-8", () => {
  // The renderer bundles Spanish translations; without an explicit charset the browser guesses,
  // and it guesses wrong on the accents.
  assert.equal(contentTypeFor("/a/index.html"), "text/html; charset=utf-8");
  assert.equal(contentTypeFor("/a/index.js"), "text/javascript; charset=utf-8");
  assert.equal(contentTypeFor("/a/index.mjs"), "text/javascript; charset=utf-8");
  assert.equal(contentTypeFor("/a/index.css"), "text/css; charset=utf-8");
  assert.equal(contentTypeFor("/a/data.json"), "application/json; charset=utf-8");
});

test("binary types do not", () => {
  assert.equal(contentTypeFor("/a/codicon.ttf"), "font/ttf");
  assert.equal(contentTypeFor("/a/icon.png"), "image/png");
  assert.equal(contentTypeFor("/a/font.woff2"), "font/woff2");
});

test("an unknown extension is not guessed at", () => {
  assert.equal(contentTypeFor("/a/thing.wasm"), "application/octet-stream");
});

test("the policy keeps the two concessions it documents, and nothing more", () => {
  // Pinned deliberately: `unsafe-eval` and `unsafe-inline` are each justified in the source, and a
  // third relaxation appearing here should be a decision someone takes on purpose.
  assert.match(CONTENT_SECURITY_POLICY, /script-src 'self' 'unsafe-eval'/);
  assert.match(CONTENT_SECURITY_POLICY, /style-src 'self' 'unsafe-inline'/);
  assert.equal(CONTENT_SECURITY_POLICY.match(/unsafe-/g)?.length, 2);
});

test("the policy allows no remote origin anywhere", () => {
  assert.equal(/https?:/.test(CONTENT_SECURITY_POLICY), false);
  assert.match(CONTENT_SECURITY_POLICY, /connect-src 'none'/);
  assert.match(CONTENT_SECURITY_POLICY, /object-src 'none'/);
  assert.match(CONTENT_SECURITY_POLICY, /base-uri 'none'/);
  assert.match(CONTENT_SECURITY_POLICY, /form-action 'none'/);
});
