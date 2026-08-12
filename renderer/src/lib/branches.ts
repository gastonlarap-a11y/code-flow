/** The shape both callers need from a branch row — structural, so `BranchInfo` stays out of `lib/`. */
export interface BranchLike {
  name: string;
  is_remote: boolean;
  is_head: boolean;
}

/**
 * The names a repository conventionally integrates into.
 *
 * There is no "default base branch" anywhere in this app: `create_pull_request` and
 * `generate_pr_description` are always handed a target the user picked. This list is the only piece
 * of knowledge that ever stood in for one, and it lived inside `CreatePrModal.tsx` — which meant the
 * ticket review, which needs exactly the same guess, would have had to copy it.
 *
 * A set, not a ranking: the winner is the first *branch* the repository lists whose name is in here,
 * so a repository holding both `develop` and `main` gets whichever git enumerates first. That is
 * what `CreatePrModal` has always done, and changing it here would have quietly changed the target
 * pre-selected in a dialog nobody asked to be touched.
 */
export const PREFERRED_TARGETS = ["main", "master", "develop", "development"];

/**
 * The branch a comparison should default to measuring against.
 *
 * A guess and nothing more, which is why every caller shows it in an editable control: a repository
 * whose integration branch is `release/2026` gets whatever comes first alphabetically, and the only
 * thing worse than guessing wrong is guessing wrong invisibly.
 *
 * Remote branches are excluded because the two callers offer local ones; `exclude` keeps the source
 * branch out of its own target list.
 */
export function preferredBaseBranch(branches: BranchLike[], exclude: string): string {
  const local = branches.filter((b) => !b.is_remote && b.name !== exclude);
  return local.find((b) => PREFERRED_TARGETS.includes(b.name))?.name ?? local[0]?.name ?? "";
}

/** The branch currently checked out, or the first local one when HEAD is detached. */
export function currentBranch(branches: BranchLike[]): string {
  const local = branches.filter((b) => !b.is_remote);
  return local.find((b) => b.is_head)?.name ?? local[0]?.name ?? "";
}
