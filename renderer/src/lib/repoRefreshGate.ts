/** The slice of repo state this decision reads — the two flags that mean "the app is mid-write". */
interface RewriteState {
  /** The branch a checkout is switching to, or null. */
  checkingOutBranch: string | null;
  /** Which of fetch/pull/push is running, if any. */
  remoteOp: "fetch" | "pull" | "push" | null;
}

/**
 * Whether a working-tree change reported by the watcher should trigger a refresh.
 *
 * **No, while the app is the one doing the writing.** A checkout or a pull rewrites the working tree
 * over many files, and `RepoWatcher` fires on the leading edge of that burst — so a refresh answering
 * it reads a tree that is halfway between two branches. Each of those operations already ends with
 * its own `refreshAll()` over the settled result, which is the reading worth having.
 *
 * That is the flicker: switching branches showed the outgoing branch's changes for a beat before
 * they vanished. `isCurrent` in `repoStore` guards the other half of the same race — a read from a
 * *previous* generation landing late — but every refresh in this window shares the generation the
 * checkout just bumped to, so nothing there could tell them apart. The difference is not when they
 * started; it is that one of them read a tree nobody should be reading yet.
 *
 * `merging` is deliberately not one of these flags. It says the repository *is* in a merge, not that
 * a merge command is running, so gating on it would silence the watcher for as long as a conflict
 * stayed unresolved — which is exactly when an external edit most needs to show up.
 */
export function watcherMayRefresh(state: RewriteState): boolean {
  return state.checkingOutBranch === null && state.remoteOp === null;
}
