import { getSetting, setSetting } from "./ipc/commands";
import type { GithubConnection } from "../types/domain";

// One list of GitHub connections (github.com and/or Enterprise hosts) persisted as a single
// app-setting JSON blob — the tokens themselves stay in the OS keychain, keyed per host. This
// is the allowlist the backend reads to know which hosts are safe to auto-detect as GitHub.
const KEY = "github_connections";

/** The canonical public GitHub host — the default a new connection is offered under. */
export const GITHUB_COM = "github.com";

export async function loadGithubConnections(): Promise<GithubConnection[]> {
  const raw = await getSetting(KEY);
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((c): c is GithubConnection => c && typeof c.host === "string");
  } catch {
    return [];
  }
}

export async function saveGithubConnections(connections: GithubConnection[]): Promise<void> {
  await setSetting(KEY, JSON.stringify(connections));
}

/** Reduces a pasted host/URL (`https://github.acme.com/…`) to a bare lowercase hostname. */
export function normalizeGithubHost(input: string): string {
  const trimmed = input.trim().replace(/\/+$/, "");
  if (!trimmed) return GITHUB_COM;
  const match = trimmed.match(/^https?:\/\/([^/]+)/i);
  return (match ? match[1]! : trimmed).toLowerCase();
}

/** A friendly label for a host — "GitHub.com" for the public host, the bare hostname otherwise. */
export function githubHostLabel(host: string): string {
  return host.toLowerCase() === GITHUB_COM ? "GitHub.com" : host;
}

/**
 * Whether a pull request's author is one of the logins this app is signed in as.
 *
 * GitHub refuses to record an approval on your own pull request (`XLANG-013`), so the button for it
 * is disabled rather than left to fail. Matching against *every* connection rather than resolving
 * the PR's own host is deliberate: a login that is yours on github.com is yours on an Enterprise
 * server too, and the cost of being wrong is asymmetric — a disabled button on someone else's PR
 * would block real work, while an enabled one on your own just reproduces today's error.
 *
 * Case-insensitive because GitHub logins are, and empty usernames are ignored: a connection saved
 * before the username was recorded would otherwise match a PR whose author string is empty.
 */
export function isOwnGithubAuthor(author: string, connections: GithubConnection[]): boolean {
  const login = author.trim().toLowerCase();
  if (!login) return false;

  return connections.some((c) => c.username.trim().toLowerCase() === login);
}
