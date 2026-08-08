import assert from "node:assert/strict";
import { test } from "node:test";
import { grants } from "./permissions";

// The bug this covers had no symptom at the seam: the handler returned `false`, Chromium rejected
// the write, and the renderer painted a checkmark regardless. A test that names the literal is the
// only thing standing between "clipboard-sanitized-write" and a typo nobody would ever see.

test("the clipboard write every copy button needs is granted", () => {
  assert.equal(grants("clipboard-sanitized-write"), true);
});

test("reading the clipboard is not", () => {
  // Writing is a click the user made. Reading is the app helping itself.
  assert.equal(grants("clipboard-read"), false);
  assert.equal(grants("deprecated-sync-clipboard-read"), false);
});

test("nothing else is", () => {
  for (const permission of [
    "geolocation",
    "media",
    "notifications",
    "midi",
    "midiSysex",
    "hid",
    "serial",
    "usb",
    "idle-detection",
    "fullscreen",
    "openExternal",
    "pointerLock",
    "display-capture",
    "fileSystem",
    "unknown",
  ]) {
    assert.equal(grants(permission), false, `${permission} should be denied`);
  }
});
