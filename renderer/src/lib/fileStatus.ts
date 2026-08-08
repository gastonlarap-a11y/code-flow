import type { TranslationKey } from "./i18n/translations";
import type { RepoStatusInfo } from "../types/domain";

// git's own vocabulary ("untracked", "typechange"...) isn't very readable to anyone who
// hasn't internalized git's internals — map each raw status to a plain-language label.
const STATUS_KEYS: Record<string, TranslationKey> = {
  untracked: "fileStatus.new",
  added: "fileStatus.added",
  modified: "fileStatus.modified",
  deleted: "fileStatus.deleted",
  renamed: "fileStatus.renamed",
  copied: "fileStatus.copied",
  typechange: "fileStatus.typechange",
  conflicted: "fileStatus.conflicted",
  ignored: "fileStatus.ignored",
  unmodified: "fileStatus.unmodified",
};

export function fileStatusLabelKey(status: string): TranslationKey {
  return STATUS_KEYS[status] ?? "fileStatus.modified";
}

export function fileStatusColor(status: string): string {
  switch (status) {
    case "added":
    case "untracked":
      return "var(--cf-success)";
    case "deleted":
      return "var(--cf-danger)";
    case "renamed":
    case "copied":
      return "var(--cf-accent)";
    default:
      return "var(--cf-warning)";
  }
}

/**
 * How many distinct files are uncommitted, counting each path once.
 *
 * A file staged *and* modified again appears in two of the four lists, and the same path counted
 * twice would read as two changes. Both the Changes tab's badge and the pre-commit analysis ask this
 * question — the analysis to know whether there is anything to analyse at all — so there is one
 * answer rather than two that can disagree.
 */
export function uncommittedCount(status: RepoStatusInfo | null): number {
  if (!status) return 0;
  const paths = new Set<string>();
  for (const list of [status.staged, status.unstaged, status.untracked, status.conflicted]) {
    for (const entry of list) paths.add(entry.path);
  }
  return paths.size;
}
