/**
 * The command that signs a CLI engine back in.
 *
 * `AUTH_EXPIRED::` says the engine lost its login; it cannot say what to type, because the fix lives
 * in a terminal the app does not own. The mapping is small, stable, and worth being exact about —
 * telling someone to run the wrong command is worse than telling them nothing, which is why an
 * engine with no such command returns `null` rather than a guess.
 *
 * Verified against the installed CLIs rather than recalled: `claude auth login` (the CLI grew an
 * `auth` subcommand with `login`/`logout`/`status`), `codex login`, `opencode auth login`. Antigravity
 * (`agy`) has **no** auth subcommand at all — its Google sign-in happens inside the tool — so it is
 * deliberately absent here.
 */
const COMMANDS: Record<string, string> = {
  claude: "claude auth login",
  codex: "codex login",
  opencode: "opencode auth login",
};

/**
 * How to re-authenticate `provider`, or `null` when no single command does it.
 *
 * `null` covers four cases that all deserve the generic wording: an engine whose sign-in is not a
 * command (`gemini`), one whose credential the app holds itself and that therefore never reports an
 * expired login (`openai`), one with no credential at all (`ollama`), and an id from a newer build
 * of the settings screen than this map knows about.
 */
export function authCommandFor(provider: string | undefined): string | null {
  return provider ? (COMMANDS[provider] ?? null) : null;
}
