import { describe, expect, test } from "vitest";
import { authCommandFor } from "./authCommand";

/**
 * The re-login commands the `AUTH_EXPIRED::` banner offers.
 *
 * These strings are read off a screen and typed into a terminal, so a wrong one costs the user a
 * failed command and the trust to try the next suggestion. They are pinned here because nothing else
 * would notice a CLI renaming its subcommand — the app never runs them.
 */

describe("the engines whose sign-in is one command", () => {
  for (const [provider, command] of [
    ["claude", "claude auth login"],
    ["codex", "codex login"],
    ["opencode", "opencode auth login"],
  ] as const) {
    test(`${provider} is signed back in with \`${command}\``, () => {
      expect(authCommandFor(provider)).toBe(command);
    });
  }
});

describe("the engines with no such command", () => {
  // Antigravity signs in through the tool itself; there is no `agy auth login` to offer, and
  // inventing one would send the user to a command that does not exist.
  test("gemini has none", () => {
    expect(authCommandFor("gemini")).toBe(null);
  });

  // Their credential is either the app's own (an API key in the keychain, with its own 401 path) or
  // absent entirely, so neither ever reports a lost login in the first place.
  for (const provider of ["openai", "ollama"]) {
    test(`${provider} has none`, () => {
      expect(authCommandFor(provider)).toBe(null);
    });
  }
});

describe("what the banner falls back on", () => {
  // The settings screen can be extended without a backend change (`AI-022`), so an id this map has
  // never heard of is expected rather than exceptional.
  test("an unknown provider gets no command rather than a guess", () => {
    expect(authCommandFor("some-future-engine")).toBe(null);
  });

  // The banner's `task` prop is optional, so the provider can genuinely be absent.
  test("no provider at all gets no command", () => {
    expect(authCommandFor(undefined)).toBe(null);
  });
});
