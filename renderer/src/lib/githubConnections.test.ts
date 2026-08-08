import { describe, expect, test } from "vitest";
import { githubHostLabel, isOwnGithubAuthor, normalizeGithubHost } from "./githubConnections";
import type { GithubConnection } from "../types/domain";

/**
 * The pure half of the GitHub connection list.
 *
 * `isOwnGithubAuthor` is what disables the Approve button, and it has consequences in both
 * directions: a false negative reproduces the raw 422 the user reported (`XLANG-013`), while a false
 * positive disables approval on someone else's pull request — which blocks real work and is the
 * worse of the two. So the cases that matter are the near-misses, not the happy path.
 */

const connections = (...usernames: string[]): GithubConnection[] =>
  usernames.map((username, index) => ({ host: index === 0 ? "github.com" : `ghe-${index}.acme.com`, username }));

describe("recognising your own pull request", () => {
  test("matches the login you are signed in as", () => {
    expect(isOwnGithubAuthor("gastonlarap-a11y", connections("gastonlarap-a11y"))).toBe(true);
  });

  test("ignores case, because GitHub logins do", () => {
    expect(isOwnGithubAuthor("GastonLaraP-A11Y", connections("gastonlarap-a11y"))).toBe(true);
  });

  test("matches a login saved for any connected host", () => {
    // The PR's own host is deliberately not resolved: a login that is yours on github.com is yours
    // on an Enterprise server too.
    expect(isOwnGithubAuthor("gaston", connections("someone-else", "gaston"))).toBe(true);
  });

  test("does not match somebody else", () => {
    expect(isOwnGithubAuthor("octocat", connections("gastonlarap-a11y"))).toBe(false);
  });

  test("does not match a login that merely contains yours", () => {
    // A substring match here would disable Approve on every PR by `gaston-bot`.
    expect(isOwnGithubAuthor("gaston-bot", connections("gaston"))).toBe(false);
  });

  test("an empty author matches nothing, even against an empty username", () => {
    // A connection saved before the username was recorded would otherwise match every PR whose
    // author string failed to come through — disabling Approve everywhere, for no visible reason.
    expect(isOwnGithubAuthor("", connections(""))).toBe(false);
    expect(isOwnGithubAuthor("   ", connections("gaston"))).toBe(false);
  });

  test("with no connections saved, nothing is yours", () => {
    expect(isOwnGithubAuthor("gaston", [])).toBe(false);
  });
});

describe("host handling", () => {
  test("a pasted URL reduces to a bare lowercase hostname", () => {
    expect(normalizeGithubHost("https://GitHub.ACME.com/")).toBe("github.acme.com");
  });

  test("an empty input falls back to the public host", () => {
    expect(normalizeGithubHost("   ")).toBe("github.com");
  });

  test("only the public host gets the friendly label", () => {
    expect(githubHostLabel("github.com")).toBe("GitHub.com");
    expect(githubHostLabel("ghe.acme.com")).toBe("ghe.acme.com");
  });
});
