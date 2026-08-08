import { create } from "zustand";
import * as api from "../lib/ipc/commands";
import { pushErrorToast, useToastStore } from "./toastStore";
import { chooseAction, tellUser } from "./confirmStore";
import { useLanguageStore } from "./languageStore";
import { translations, type TranslationKey } from "../lib/i18n/translations";
import { uncommittedCount } from "../lib/fileStatus";
import type {
  BranchInfo,
  CommitFileInfo,
  CommitInfo,
  ConflictFile,
  FileDiffInfo,
  MergeOutcome,
  RemoteInfo,
  RepoStatusInfo,
  StashApplyOutcome,
  StashInfo,
} from "../types/domain";

interface RepoState {
  repoPath: string | null;
  status: RepoStatusInfo | null;
  branches: BranchInfo[];
  commits: CommitInfo[];
  unpushedCommits: CommitInfo[];
  stashes: StashInfo[];
  remotes: RemoteInfo[];
  selectedCommitId: string | null;
  /** The files the expanded commit touched — names and statuses only, no content (GIT-035). */
  commitFiles: CommitFileInfo[];
  commitFilesLoading: boolean;
  /** Which file of the expanded commit is open, keyed by `new_path ?? old_path`. */
  selectedCommitFile: string | null;
  /** That one file's diff, kept as a list so it goes straight into `DiffView`. */
  commitFileDiff: FileDiffInfo[];
  commitFileDiffLoading: boolean;
  workingDiff: FileDiffInfo[];
  stagedDiff: FileDiffInfo[];
  busy: boolean;
  error: string | null;
  checkingOutBranch: string | null;
  /** Which of fetch/pull/push is currently running, if any — the three are mutually
   * exclusive so the status bar can show a single loader and block the other two. */
  remoteOp: "fetch" | "pull" | "push" | null;
  merging: boolean;
  conflicts: ConflictFile[];
  commitsLoading: boolean;
  /**
   * Bumped whenever what the repo *is* changes under the same path — today, a checkout.
   *
   * Loads capture it on entry and drop their result if it moved, which is what stops a read of the
   * outgoing branch from landing after the incoming one. See `isCurrent`.
   */
  refreshSeq: number;
  /** True from the moment a repo is selected until every piece of its sidebar data
   * (branches, stashes, remotes, merge state…) has landed — lets the sidebar show one
   * skeleton and reveal everything together instead of each section popping in as its
   * own fetch happens to resolve. */
  projectLoading: boolean;

  setRepoPath: (path: string | null) => Promise<void>;
  refreshAll: () => Promise<void>;
  refreshStatus: () => Promise<void>;
  refreshBranches: () => Promise<void>;
  refreshCommits: () => Promise<void>;
  refreshUnpushedCommits: () => Promise<void>;
  refreshStashes: () => Promise<void>;
  refreshRemotes: () => Promise<void>;
  refreshMergeState: () => Promise<void>;
  selectCommit: (id: string | null) => Promise<void>;
  selectCommitFile: (file: CommitFileInfo | null) => Promise<void>;

  mergeBranch: (branchName: string) => Promise<MergeOutcome | null>;
  resolveConflict: (relPath: string, side: "ours" | "theirs") => Promise<void>;
  markConflictResolved: (relPath: string) => Promise<void>;
  completeMerge: (message: string) => Promise<void>;
  abortMerge: () => Promise<void>;

  stageFile: (filePath: string) => Promise<void>;
  unstageFile: (filePath: string) => Promise<void>;
  stageAll: () => Promise<void>;
  unstageAll: () => Promise<void>;
  discardFile: (filePath: string) => Promise<void>;
  discardAll: () => Promise<void>;
  commitChanges: (message: string) => Promise<void>;

  checkoutBranch: (name: string) => Promise<void>;
  checkoutDetached: (refname: string) => Promise<void>;
  checkoutRemoteBranch: (remoteBranch: string) => Promise<void>;
  createBranch: (name: string, startPoint?: string) => Promise<void>;
  deleteBranch: (name: string, isRemote: boolean) => Promise<void>;
  setRemoteUrl: (name: string, url: string) => Promise<void>;
  undoCommit: (commitId: string) => Promise<void>;
  discardConflicted: () => Promise<void>;

  stashSave: (message?: string, includeUntracked?: boolean) => Promise<void>;
  stashApply: (index: number) => Promise<void>;
  stashPop: (index: number) => Promise<void>;
  stashDrop: (index: number) => Promise<void>;
  renameStash: (index: number, newMessage: string) => Promise<void>;

  fetch: () => Promise<void>;
  pull: () => Promise<void>;
  push: (setUpstream?: boolean) => Promise<void>;
}

async function guarded(
  set: (partial: Partial<RepoState>) => void,
  fn: () => Promise<void>,
  refreshLabel?: TranslationKey,
) {
  set({ busy: true, error: null });
  try {
    await (refreshLabel ? withOneRetry(fn) : fn());
  } catch (e) {
    const message = refreshLabel ? `${translate(refreshLabel)}: ${String(e)}` : String(e);
    set({ error: message });
    pushErrorToast(message);
  } finally {
    set({ busy: false });
  }
}

/**
 * Whether a load started at generation `seq` may still write what it read.
 *
 * Two ways to be stale, and both have happened. **Another repo**: these read `repoPath` on entry and
 * wrote unconditionally on resolve, so clicking project A then B before A's fetch settled overwrote
 * B's branches with A's. **Another branch**: a checkout does not change `repoPath`, so that check
 * alone let a read of the *old* branch land after the new one — `RepoWatcher` fires
 * `repo:fs-changed` on the leading edge of the burst a checkout creates, and `App.tsx` answers it
 * with a `refreshAll()` that reads a half-written working tree. That is the flicker: the outgoing
 * branch's files showing for a beat after switching.
 */
function isCurrent(get: () => RepoState, repoPath: string, seq: number): boolean {
  return get().repoPath === repoPath && get().refreshSeq === seq;
}

/** How long a background refresh waits before it tries once more, in ms. */
const REFRESH_RETRY_DELAY_MS = 300;

/**
 * Retries a read-only load exactly once, after a short delay, before treating it as a failure.
 *
 * Scoped to post-mutation background refreshes only, never to a mutating command: those are racing
 * a `git` child process's own file-system effects settling right after a pull/checkout/merge that
 * already succeeded, so one retry across that window is a defensive measure against that specific
 * timing, not a general resilience policy. Retrying a mutation (checkout, stash apply, commit)
 * automatically would risk doubling an effect that only *looked* like it failed.
 */
async function withOneRetry<T>(load: () => Promise<T>): Promise<T> {
  try {
    return await load();
  } catch {
    await new Promise((resolve) => setTimeout(resolve, REFRESH_RETRY_DELAY_MS));
    return await load();
  }
}

/**
 * Loads one slice of repo state, and refuses to write it when it is no longer current.
 *
 * A failure is reported rather than thrown. `refreshAll` runs these through `Promise.all`, so one
 * rejection there took down the whole batch and escaped as an unhandled rejection — no toast, and
 * `setRepoPath` never reaching the line that clears `projectLoading`, which left the sidebar
 * showing its skeleton until the project was closed and reopened.
 *
 * `busy` is deliberately untouched: seven of these run concurrently, and a shared boolean that the
 * first one to finish clears says nothing useful about the other six.
 *
 * `refreshLabel`, when given, names what this refresh does, so a failure here — after a pull,
 * fetch or checkout that already succeeded — reads as "couldn't refresh X" and never as "the
 * pull/checkout failed"; it also turns on one retry (`withOneRetry`) to absorb a transient read
 * right after the mutation before giving up. Left out for the on-demand, click-triggered loads
 * (a commit's file list, a file's diff) that aren't racing a just-finished mutation.
 */
async function refreshing<T>(
  set: (partial: Partial<RepoState>) => void,
  get: () => RepoState,
  refreshLabel: TranslationKey | undefined,
  load: (repoPath: string) => Promise<T>,
  apply: (value: T) => Partial<RepoState>,
): Promise<void> {
  const repoPath = get().repoPath;
  if (!repoPath) return;
  const seq = get().refreshSeq;

  try {
    const value = await (refreshLabel ? withOneRetry(() => load(repoPath)) : load(repoPath));
    if (!isCurrent(get, repoPath, seq)) return;
    set(apply(value));
  } catch (e) {
    const message = refreshLabel ? `${translate(refreshLabel)}: ${String(e)}` : String(e);
    set({ error: message });
    pushErrorToast(message);
  }
}

/** Translates outside of React (this store isn't a component) using whatever language is
 * currently selected — same lookup `useT()` does, just without the hook. */
function translate(key: TranslationKey, params?: Record<string, string>): string {
  const language = useLanguageStore.getState().language;
  const raw: string = translations[language][key] ?? translations.en[key] ?? key;
  if (!params) return raw;
  return Object.entries(params).reduce((acc, [name, value]) => acc.split(`{${name}}`).join(value), raw);
}

/** Set by the sidecar on the one checkout failure that has a way out — see
 * `CHECKOUT_CONFLICT_PREFIX` in `Branches.cs`. */
const CHECKOUT_CONFLICT_PREFIX = "CHECKOUT_CONFLICT: ";

/**
 * Runs a checkout and, when uncommitted work is what blocks it, offers a way through instead of
 * just reporting the failure.
 *
 * Three answers, because there are three different things a person means here: **carry** the work
 * to the other branch (stash, switch, pop it back — what `git checkout` does on its own when
 * nothing collides), **park** it in a stash and switch without it, or **cancel**. Cancelling used
 * to re-throw the original error, so declining an offer looked like something had gone wrong.
 */
async function checkoutGuarded(
  set: (partial: Partial<RepoState>) => void,
  get: () => RepoState,
  target: string,
  run: () => Promise<void>,
) {
  const { repoPath } = get();
  if (!repoPath) return;
  set({
    checkingOutBranch: target,
    busy: true,
    error: null,
    // Anything already in flight now describes the branch being left, so it must not land. And the
    // working tree on screen belongs to that branch too — keeping it up while the checkout runs is
    // what made the old branch's files flash before the new state arrived. `ChangesPanel` reads
    // `checkingOutBranch` to show a skeleton rather than "no repository" in the gap.
    refreshSeq: get().refreshSeq + 1,
    status: null,
    workingDiff: [],
    stagedDiff: [],
  });
  try {
    try {
      await run();
    } catch (e) {
      if (!String(e).includes(CHECKOUT_CONFLICT_PREFIX)) throw e;

      const choice = await chooseAction(translate("checkout.blockedByChanges", { name: target }), [
        { id: "carry", label: translate("checkout.carryChanges") },
        { id: "stash", label: translate("checkout.stashAndSwitch"), variant: "ghost" },
      ]);
      // A cancel is an answer, not a failure: leave the repo exactly as it was, quietly.
      if (choice === null) return;

      await api.stashSave(repoPath, translate("checkout.autoStashMessage", { name: target }), true);
      try {
        await run();
      } catch (checkoutError) {
        // The work is in the stash and the branch never changed. Say where it is — that is the
        // difference between "I lost my changes" and "they are one click away".
        throw new Error(`${String(checkoutError)} — ${translate("checkout.changesAreStashed")}`, {
          cause: checkoutError,
        });
      }

      if (choice === "carry") {
        await carryStashOver(get);
      } else {
        useToastStore.getState().pushToast(translate("checkout.changesStashed"), "info");
      }
    }
    await get().refreshAll();
  } catch (e) {
    const message = String(e).replace(CHECKOUT_CONFLICT_PREFIX, "");
    set({ error: message });
    pushErrorToast(message);
  } finally {
    set({ busy: false, checkingOutBranch: null });
  }
}

/**
 * Says what applying a stash actually did.
 *
 * `"applied"` alone does not mean anything arrived: applying over content the branch already has
 * succeeds and changes nothing. That case gets a dialog rather than a toast — it leaves the Changes
 * panel empty, which is indistinguishable from having lost the work unless someone says otherwise.
 */
async function announceStashOutcome(get: () => RepoState, outcome: StashApplyOutcome) {
  if (outcome === "conflicts") {
    useToastStore.getState().pushToast(translate("stash.appliedWithConflicts"), "info");
    return;
  }
  if (outcome !== "applied") {
    useToastStore.getState().pushToast(translate("stash.notApplied"), "info");
    return;
  }
  if (uncommittedCount(get().status) === 0) {
    await tellUser(translate("stash.nothingArrived"), translate("common.gotIt"));
  }
}

/**
 * Applies the stash the checkout just made onto the branch now checked out.
 *
 * **`stash_apply`, never `stash_pop`.** Pop deletes the entry the moment it applies cleanly, and
 * for the length of that operation the only copy of the user's work is the working tree. Worse,
 * when the destination branch already contains that content the apply changes nothing, so popping
 * threw the backup away and brought nothing across — which is exactly how a day's work looked lost.
 * The entry stays in the list until the user drops it from the sidebar.
 *
 * A conflicting apply is **not** an error (`GIT-015`): the index is marked, the stash is intact,
 * and the conflict UI takes over — `refreshAll` is what puts it on screen.
 */
async function carryStashOver(get: () => RepoState) {
  const { repoPath } = get();
  if (!repoPath) return;

  const outcome = await api.stashApply(repoPath, 0);
  const toast = useToastStore.getState().pushToast;

  if (outcome === "conflicts") {
    toast(translate("checkout.carriedWithConflicts"), "info");
    return;
  }
  if (outcome !== "applied") {
    // Nothing was applied, and the stash is untouched — the only thing worth saying here.
    throw new Error(translate("checkout.carryFailed"));
  }

  // The stash took everything with it, untracked included, so the tree was clean the moment the
  // checkout finished: anything here now is what the apply brought. Still nothing means the branch
  // already had this content — and saying "your changes came along" there is a lie the user then
  // has to disprove by hand.
  await get().refreshStatus();
  if (uncommittedCount(get().status) > 0) {
    toast(translate("checkout.changesCarried"), "success");
    return;
  }
  await tellUser(translate("checkout.nothingToCarry"), translate("common.gotIt"));
}

export const useRepoStore = create<RepoState>((set, get) => ({
  repoPath: null,
  status: null,
  branches: [],
  commits: [],
  unpushedCommits: [],
  stashes: [],
  remotes: [],
  selectedCommitId: null,
  commitFiles: [],
  commitFilesLoading: false,
  selectedCommitFile: null,
  commitFileDiff: [],
  commitFileDiffLoading: false,
  workingDiff: [],
  stagedDiff: [],
  busy: false,
  error: null,
  checkingOutBranch: null,
  remoteOp: null,
  merging: false,
  conflicts: [],
  commitsLoading: false,
  projectLoading: false,
  refreshSeq: 0,

  setRepoPath: async (path) => {
    set({
      repoPath: path,
      projectLoading: Boolean(path),
      status: null,
      branches: [],
      commits: [],
      unpushedCommits: [],
      stashes: [],
      remotes: [],
      selectedCommitId: null,
      commitFiles: [],
      commitFilesLoading: false,
      selectedCommitFile: null,
      commitFileDiff: [],
      commitFileDiffLoading: false,
      workingDiff: [],
      stagedDiff: [],
      merging: false,
      conflicts: [],
    });
    if (path) {
      try {
        await get().refreshAll();
      } finally {
        // In a `finally` because this is the only thing that clears the sidebar's skeleton. Each
        // refresher reports its own failures now, but a rejection that escapes anyway must not
        // leave the project looking like it is still loading forever.
        //
        // Guards against a stale resolution: if the user already switched to another repo
        // while this fetch was in flight, don't clear the new repo's loading state.
        if (get().repoPath === path) set({ projectLoading: false });
      }
    }
  },

  refreshAll: async () => {
    await Promise.all([
      get().refreshStatus(),
      get().refreshBranches(),
      get().refreshCommits(),
      get().refreshUnpushedCommits(),
      get().refreshStashes(),
      get().refreshRemotes(),
      get().refreshMergeState(),
    ]);
  },

  refreshStatus: async () => {
    const { repoPath, refreshSeq } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      const [status, workingDiff, stagedDiff] = await Promise.all([
        api.getStatus(repoPath),
        api.getWorkingDiff(repoPath),
        api.getStagedDiff(repoPath),
      ]);
      // The one read that shows the working tree itself, so a stale answer here is the one the user
      // actually sees — the outgoing branch's files reappearing for a beat after a checkout.
      if (!isCurrent(get, repoPath, refreshSeq)) return;
      set({ status, workingDiff, stagedDiff });
    }, "refresh.status");
  },

  refreshBranches: () =>
    refreshing(set, get, "refresh.branches", api.listBranches, (branches) => ({ branches })),

  refreshCommits: async () => {
    set({ commitsLoading: true });
    try {
      // This one keeps its own flag: it is the slow query and the commit list renders a spinner.
      await refreshing(
        set, get, "refresh.commits", (path) => api.listCommits(path, true, 500), (commits) => ({ commits }),
      );
    } finally {
      set({ commitsLoading: false });
    }
  },

  refreshUnpushedCommits: () =>
    refreshing(
      set, get, "refresh.unpushedCommits", api.listUnpushedCommits, (unpushedCommits) => ({ unpushedCommits }),
    ),

  refreshStashes: () => refreshing(set, get, "refresh.stashes", api.listStashes, (stashes) => ({ stashes })),

  refreshRemotes: () => refreshing(set, get, "refresh.remotes", api.listRemotes, (remotes) => ({ remotes })),

  // Conflicts are read unconditionally, not only while merging. `is_merging` is `MERGE_HEAD` and
  // nothing else, while `list_conflicts` reads the index — and a stash that conflicts marks the
  // index without ever writing `MERGE_HEAD`. Gating on `merging` is what left a repository full of
  // conflict markers with no UI offering to resolve them (GIT-019).
  refreshMergeState: () =>
    refreshing(
      set,
      get,
      "refresh.mergeState",
      async (path) => {
        const [merging, conflicts] = await Promise.all([api.isMerging(path), api.listConflicts(path)]);
        return { merging, conflicts };
      },
      (state) => state,
    ),

  mergeBranch: async (branchName) => {
    const { repoPath } = get();
    if (!repoPath) return null;
    let outcome: MergeOutcome | null = null;
    await guarded(set, async () => {
      outcome = await api.mergeBranch(repoPath, branchName);
      await get().refreshAll();
    });
    return outcome;
  },

  resolveConflict: async (relPath, side) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.resolveConflictSide(repoPath, relPath, side);
      await Promise.all([get().refreshMergeState(), get().refreshStatus()]);
    });
  },

  markConflictResolved: async (relPath) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.markConflictResolved(repoPath, relPath);
      await Promise.all([get().refreshMergeState(), get().refreshStatus()]);
    });
  },

  completeMerge: async (message) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.completeMerge(repoPath, message);
      await get().refreshAll();
    });
  },

  abortMerge: async () => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.abortMerge(repoPath);
      await get().refreshAll();
    });
  },

  /** Expands a commit into its file list. No diff is fetched here — that is `selectCommitFile`. */
  selectCommit: async (id) => {
    set({
      selectedCommitId: id,
      commitFiles: [],
      commitFilesLoading: Boolean(id),
      selectedCommitFile: null,
      commitFileDiff: [],
      commitFileDiffLoading: false,
    });
    if (!id) return;

    await refreshing(
      set,
      get,
      undefined,
      (path) => api.listCommitFiles(path, id),
      // Also checked against the selection, not just the repo: clicking through the graph faster
      // than the file lists load would otherwise list one commit's files under another's row.
      (commitFiles) => (get().selectedCommitId === id ? { commitFiles } : {}),
    );
    // Outside `apply` so a failure clears the spinner too — `refreshing` reports the error and
    // never reaches `apply`, which would leave the expanded row loading forever.
    if (get().selectedCommitId === id) set({ commitFilesLoading: false });
  },

  selectCommitFile: async (file) => {
    const path = file ? (file.new_path ?? file.old_path) : null;
    const oid = get().selectedCommitId;
    set({ selectedCommitFile: path, commitFileDiff: [], commitFileDiffLoading: Boolean(path) });
    if (!path || !oid) return;

    const oldPath = file?.old_path ?? null;
    await refreshing(
      set,
      get,
      undefined,
      (repoPath) => api.getCommitFileDiff(repoPath, oid, path, oldPath),
      // Both halves of the selection are checked: clicking from one file of a commit to the next
      // faster than they load would otherwise paint the first file's diff under the second's header.
      (commitFileDiff) =>
        get().selectedCommitId === oid && get().selectedCommitFile === path ? { commitFileDiff } : {},
    );
    if (get().selectedCommitId === oid && get().selectedCommitFile === path) {
      set({ commitFileDiffLoading: false });
    }
  },

  stageFile: async (filePath) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.stageFile(repoPath, filePath);
      await get().refreshStatus();
    });
  },

  unstageFile: async (filePath) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.unstageFile(repoPath, filePath);
      await get().refreshStatus();
    });
  },

  stageAll: async () => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.stageAll(repoPath);
      await get().refreshStatus();
    });
  },

  unstageAll: async () => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.unstageAll(repoPath);
      await get().refreshStatus();
    });
  },

  discardFile: async (filePath) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.discardFileChanges(repoPath, filePath);
      await get().refreshStatus();
    });
  },

  discardAll: async () => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.discardAllChanges(repoPath);
      await get().refreshStatus();
    });
  },

  commitChanges: async (message) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.commitChanges(repoPath, message);
      await get().refreshAll();
    });
  },

  checkoutBranch: async (name) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await checkoutGuarded(set, get, name, () => api.checkoutLocalBranch(repoPath, name));
  },

  checkoutDetached: async (refname) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await checkoutGuarded(set, get, refname, () => api.checkoutDetached(repoPath, refname));
  },

  checkoutRemoteBranch: async (remoteBranch) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await checkoutGuarded(set, get, remoteBranch, async () => {
      await api.checkoutRemoteTracking(repoPath, remoteBranch);
    });
  },

  createBranch: async (name, startPoint) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.createBranch(repoPath, name, startPoint);
      await get().refreshBranches();
    });
  },

  deleteBranch: async (name, isRemote) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.deleteBranch(repoPath, name, isRemote);
      await get().refreshBranches();
    });
  },

  setRemoteUrl: async (name, url) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.setRemoteUrl(repoPath, name, url);
      await get().refreshRemotes();
    });
  },

  undoCommit: async (commitId) => {
    const { repoPath, commits } = get();
    if (!repoPath) return;
    const commit = commits.find((c) => c.id === commitId);
    if (!commit || commit.parent_ids.length === 0) return;
    await guarded(set, async () => {
      // `commit.parent_ids.length === 0` returned above, so a first parent always exists here.
      await api.resetToCommit(repoPath, commit.parent_ids[0]!, "mixed");
      await get().refreshAll();
    });
  },

  /**
   * Throws away a conflicted working tree that did not come from a merge.
   *
   * A hard reset to HEAD, not `abort_merge`: that one is the same reset but also clears merge
   * state it has no business touching outside a merge (GIT-018). Recoverable by design — the stash
   * these conflicts came from is only dropped by libgit2 once it applies cleanly (GIT-015).
   */
  discardConflicted: async () => {
    const { repoPath, branches } = get();
    const head = branches.find((b) => b.is_head)?.target;
    if (!repoPath || !head) return;
    await guarded(set, async () => {
      await api.resetToCommit(repoPath, head, "hard");
      await get().refreshAll();
    });
  },

  stashSave: async (message, includeUntracked = false) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.stashSave(repoPath, message, includeUntracked);
      await get().refreshAll();
    });
  },

  // Both report a conflicting outcome instead of finishing quietly: the sidecar returns it rather
  // than throwing (GIT-015), so without this the sidebar's Apply looked like it had worked while
  // it had actually left conflict markers on disk.
  stashApply: async (index) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      const outcome = await api.stashApply(repoPath, index);
      await get().refreshAll();
      await announceStashOutcome(get, outcome);
    });
  },

  stashPop: async (index) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      const outcome = await api.stashPop(repoPath, index);
      await get().refreshAll();
      await announceStashOutcome(get, outcome);
    });
  },

  stashDrop: async (index) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.stashDrop(repoPath, index);
      await get().refreshStashes();
    });
  },

  renameStash: async (index, newMessage) => {
    const { repoPath } = get();
    if (!repoPath) return;
    await guarded(set, async () => {
      await api.renameStash(repoPath, index, newMessage);
      await get().refreshStashes();
    });
  },

  fetch: async () => {
    const { repoPath, remoteOp } = get();
    if (!repoPath || remoteOp) return;
    set({ remoteOp: "fetch" });
    try {
      await api.gitFetch(repoPath);
      await get().refreshBranches();
    } catch (e) {
      const message = String(e);
      set({ error: message });
      pushErrorToast(message);
    } finally {
      set({ remoteOp: null });
    }
  },

  pull: async () => {
    const { repoPath, remoteOp } = get();
    if (!repoPath || remoteOp) return;
    set({ remoteOp: "pull" });
    try {
      await guarded(set, async () => {
        await api.gitPull(repoPath);
        await get().refreshAll();
      });
    } finally {
      set({ remoteOp: null });
    }
  },

  push: async (setUpstream = false) => {
    const { repoPath, remoteOp } = get();
    if (!repoPath || remoteOp) return;
    set({ remoteOp: "push" });
    try {
      await guarded(set, async () => {
        await api.gitPush(repoPath, setUpstream);
        await Promise.all([get().refreshBranches(), get().refreshUnpushedCommits()]);
      });
    } finally {
      set({ remoteOp: null });
    }
  },
}));
