export interface Workspace {
  id: string;
  name: string;
  icon: string;
  color: string;
  sort_order: number;
  created_at: string;
  /** Commit-identity override (WS-008): both null means "use the global git identity". */
  git_name: string | null;
  git_email: string | null;
  /** Which Azure DevOps organisation this workspace's tickets come from. Null means the user has
   * not chosen, and the resolution falls through to the linked project's own organisation. */
  ado_org: string | null;
  /** Which project inside it holds the board. Null falls through to the repository's own — which a
   * GitHub-hosted repository does not have, so this is the only place it can be said. */
  ado_project: string | null;
}

export interface Project {
  id: string;
  workspace_id: string;
  name: string;
  local_path: string;
  remote_url: string | null;
  color: string;
  icon: string;
  ado_org: string | null;
  ado_project: string | null;
  ado_repo_id: string | null;
  github_owner: string | null;
  github_repo: string | null;
  github_host: string | null;
  sort_order: number;
  created_at: string;
}

export interface NewProject {
  workspace_id: string;
  name: string;
  local_path: string;
  remote_url: string | null;
  color: string;
  icon: string;
  ado_org: string | null;
  ado_project: string | null;
  ado_repo_id: string | null;
  github_owner: string | null;
  github_repo: string | null;
  github_host: string | null;
}

/** A saved GitHub connection — one per host (github.com or an Enterprise Server). Persisted as
 * the `github_connections` app-setting (JSON); the token itself lives in the OS keychain. */
export interface GithubConnection {
  host: string;
  username: string;
}

/** A saved Azure DevOps connection — one per organization. Persisted as the `ado_connections`
 * app-setting (JSON); the PAT itself lives in the OS keychain, keyed per org. */
export interface AdoConnection {
  org: string;
}

export interface FileStatusEntry {
  path: string;
  status: string;
}

export interface RepoStatusInfo {
  staged: FileStatusEntry[];
  unstaged: FileStatusEntry[];
  untracked: FileStatusEntry[];
  conflicted: FileStatusEntry[];
  current_branch: string | null;
  is_detached: boolean;
}

export interface CommitInfo {
  id: string;
  short_id: string;
  summary: string;
  author_name: string;
  author_email: string;
  timestamp: number;
  parent_ids: string[];
  refs: string[];
}

export interface BranchInfo {
  name: string;
  is_head: boolean;
  is_remote: boolean;
  upstream: string | null;
  ahead: number;
  behind: number;
  target: string | null;
}

export interface StashInfo {
  index: number;
  message: string;
  oid: string;
}

export interface RemoteInfo {
  name: string;
  url: string;
}

export interface GitIdentity {
  name: string | null;
  email: string | null;
}

export interface FileEntry {
  name: string;
  path: string;
  is_dir: boolean;
}

export interface MergeOutcome {
  status: "up_to_date" | "fast_forward" | "merged" | "conflicts";
  conflicts: string[];
}

export interface ConflictFile {
  path: string;
}

export interface DiffLine {
  origin: string;
  content: string;
  old_lineno: number | null;
  new_lineno: number | null;
}

export interface DiffHunkInfo {
  header: string;
  lines: DiffLine[];
}

export interface FileDiffInfo {
  old_path: string | null;
  new_path: string | null;
  status: string;
  hunks: DiffHunkInfo[];
}

/**
 * How applying a stash went (GIT-015).
 *
 * Only `"applied"` removed the entry — libgit2 leaves the stash in place for every other outcome,
 * which is what makes a conflicting pop recoverable rather than a loss.
 */
export type StashApplyOutcome = "applied" | "conflicts" | "not_found" | "uncommitted_changes" | "unknown";

/** One file a commit touched, without its diff — what the graph expands a commit into (GIT-035). */
export interface CommitFileInfo {
  old_path: string | null;
  new_path: string | null;
  status: string;
}

/** A credential-looking match found in the staged diff by the pre-commit secret scanner. */
export interface SecretHit {
  file: string;
  line: number;
  rule: string;
  rule_name: string;
  severity: "critical" | "warning";
  preview: string;
}

export interface ReviewContext {
  id: string;
  workspace_id: string;
  name: string;
  content: string;
  enabled: boolean;
  created_at: string;
}

/** A saved PR-review run as listed in the memory manager (slim projection, no heavy text). */
export interface ReviewRunSummary {
  id: string;
  project_id: string;
  project_name: string;
  pr_id: number;
  pr_title: string;
  iter: number;
  level: string;
  findings_count: number;
  created_at: string;
}

/** One finding inside a saved run's `findings` JSON (mirrors the sidecar's `MemoryFinding`). */
export interface SavedFinding {
  id: string;
  severity: string;
  tipo: string;
  categoria: string;
  subtitulo: string;
  archivo: string | null;
  lineas: string | null;
  confianza: number | null;
  estado: string;
  thread_id?: number | null;
  introducido_en_iter: number;
  resuelto_en_iter?: number | null;
  motivo_descarte?: string | null;
  delta?: string | null;
}

/** Full content of one saved review run, for the in-app viewer / export. */
export interface ReviewRunDetail {
  id: string;
  project_id: string;
  pr_id: number;
  iter: number;
  level: string;
  /** Run metadata as a JSON string (ReviewMeta). */
  meta: string;
  review_md: string;
  diff: string;
  /** Parsed findings as a JSON string. */
  findings: string;
  created_at: string;
}

/** A user-defined SDD/Harness agent (role): name, role, model, prompt, on/off. */
export interface WorkspaceAgent {
  id: string;
  workspace_id: string;
  name: string;
  role: string;
  provider: string;
  model: string;
  prompt: string;
  enabled: boolean;
  sort_order: number;
  created_at: string;
}

export interface WorkspaceSkill {
  id: string;
  workspace_id: string;
  skill_name: string;
  source_repo: string;
  enabled: boolean;
  installed_at: string;
}

export interface WorkspaceMcp {
  id: string;
  workspace_id: string;
  name: string;
  command: string;
  args: string;
  env: string;
  enabled: boolean;
  created_at: string;
}

export interface ActivityLogEntry {
  id: string;
  project_id: string;
  /** The conversation this turn belongs to (app-minted, stable), not the engine's session token. */
  session_id: string | null;
  /** The engine's resume token for this turn, when it reported one — used to carry a reopened
   * conversation forward on the CLI's side. */
  engine_session_id: string | null;
  question: string;
  answer: string;
  /** JSON array of `{stream, line}` with what the engine printed while working on this turn, so a
   * finished answer can still show how it got there. `null` for turns recorded before traces. */
  trace: string | null;
  created_at: string;
  /** How long the engine took to answer, in ms. `null` for turns recorded before this was tracked. */
  response_time_ms: number | null;
  /** True when the turn failed — `answer` then holds the engine's error text. */
  is_error: boolean;
  /** Provider id that answered this turn (`claude`, `codex`, …), recorded at the time it ran so a
   * reopened conversation isn't relabelled by today's routing. `null` for older turns. */
  provider: string | null;
  /** Model the CLI reported for this turn. `null` for older turns, or when it didn't report one. */
  model: string | null;
  /** Version of the engine CLI that answered this turn. `null` for older turns. */
  engine_version: string | null;
}

export interface JobHistoryEntry {
  id: string;
  project_id: string;
  kind: string;
  label: string;
  custom_label: string | null;
  status: string;
  result: string | null;
  error: string | null;
  meta: string;
  created_at: string;
}

export interface ChatConversationSummary {
  session_id: string;
  project_id: string;
  title: string;
  created_at: string;
  updated_at: string;
  turn_count: number;
}

export interface PrThreadComment {
  author: string;
  content: string;
  published_date: string;
}

export interface PrCommentThread {
  id: number;
  file_path: string | null;
  start_line: number | null;
  end_line: number | null;
  comments: PrThreadComment[];
}

export interface GitProgressEvent {
  op: string;
  line: string;
}

export interface GitDoneEvent {
  op: string;
  success: boolean;
  message: string;
}

export type ThemePreference = "light" | "dark" | "system";

export interface AdoProject {
  id: string;
  name: string;
}

export interface AdoRepo {
  id: string;
  name: string;
}

export type VcsProvider = "azure" | "github";

/** AI-drafted PR title + body, returned by `generate_pr_description` to prefill the create form. */
export interface PrDescriptionDraft {
  title: string;
  body: string;
}

export interface PullRequestSummary {
  id: number;
  title: string;
  description: string;
  status: "open" | "draft" | "merged" | "closed";
  source_branch: string;
  target_branch: string;
  author: string;
  created_at: string;
  url: string;
  /** Which host this PR came from — drives the "view on…" link and post-confirmation copy. */
  provider: VcsProvider;
}

export type AutoLinkResult =
  | { status: "Linked"; project: Project }
  | { status: "NeedsToken"; provider: VcsProvider; identifier: string }
  | { status: "NotDetected" };

/** The decision the signed-in user has already recorded on a pull request, as its host reports it
 * — so a vote cast on the website counts the same as one cast here. Drives whether the approve /
 * request-changes buttons are still offered. */
export type PrDecision = "approved" | "changes_requested" | "none";

/** What a PR decision left behind: the pull request as the host now reports it, and the Activity
 * row the action was filed under. */
export interface PrActionOutcome {
  pr: PullRequestSummary;
  activity: JobHistoryEntry;
}

/** What a pasted pull-request link turned out to be. `Ready` is the happy path: the PR was read
 * from its host *and* matched to a local repository (linked on the spot if it wasn't already), so
 * everything downstream — diff, findings, comments, review memory — works exactly as it does for
 * a PR picked from the sidebar. */
export type PrLinkResolution =
  | {
      status: "Ready";
      project_id: string;
      workspace_id: string;
      project_name: string;
      pr: PullRequestSummary;
    }
  | { status: "NeedsToken"; provider: VcsProvider; identifier: string }
  /** A credential is saved and the host refused it — a different sentence from "connect this
   * organisation", because the user has already done that. Azure DevOps only for now. */
  | { status: "Expired"; provider: VcsProvider; identifier: string }
  | {
      status: "NoLocalRepo";
      provider: VcsProvider;
      repo_label: string;
      /** Clone URL for the "clone it and review" offer. */
      clone_url: string;
      pr: PullRequestSummary;
    }
  | { status: "Unrecognized" };

// ---------------------------------------------------------------------------
// Work items (tickets)
// ---------------------------------------------------------------------------

/** A cached work item. `mirror_path` is where its readable copy lives on disk. */
export interface Ticket {
  id: string;
  provider: string;
  org: string;
  project: string;
  /** Text, not a number: Azure numbers work items and Jira names them ("PROJ-45"). */
  external_id: string;
  title: string;
  state: string;
  work_item_type: string;
  assigned_to: string | null;
  web_url: string;
  rev: number;
  mirror_path: string;
  synced_at: string;
}

/** One row of a ticket picker — what a list shows, fetched without the rest of the work item. */
export interface TicketSummary {
  external_id: string;
  title: string;
  state: string;
  work_item_type: string;
  assigned_to: string | null;
}

/** How the Azure account for a project's tickets was decided. `none` means it was not: the UI has
 * to ask, because guessing shows the wrong board's (empty) list and blames the board. */
export type TicketAccountSource = "workspace" | "project" | "only_connection" | "none";

export interface TicketAccount {
  org: string | null;
  project: string | null;
  source: TicketAccountSource;
}

/** Where a ticket's requirements were found, and in what shape.
 * `list`: an enumerable list, already numbered in `items`.
 * `prose`: narrative — the model enumerates it, because splitting prose by regex cuts criteria in half.
 * `none`: no field carried enough content to be a requirement, which the review says out loud. */
export type TicketCriteriaMode = "list" | "prose" | "none";

export interface TicketCriteria {
  mode: TicketCriteriaMode;
  /** The Azure reference name the content came from, e.g. `System.Description`. */
  field: string | null;
  markdown: string;
  items: string[];
}

/** Where a cached ticket is linked: one per branch it is work for. */
export interface TicketLink {
  project_id: string;
  /** The repository's own name — an id on screen answers nothing. */
  project_name: string;
  branch: string;
}

/**
 * A cached ticket and the branches it is work for.
 *
 * The links ride with it because `list_tickets` is workspace-wide: a row that does not say which
 * repository and branch it belongs to is unreadable as soon as a workspace holds two repositories.
 * Genuinely plural — `ticket_links` is keyed `(project_id, branch)`.
 */
export interface TicketWithLinks {
  ticket: Ticket;
  links: TicketLink[];
}

/** A ticket a branch name appears to be about. A suggestion, never a link. */
export interface TicketSuggestion {
  provider: string;
  external_id: string;
}

/** A work item address parsed out of pasted text. `org`/`project` are null for a bare id. */
export interface TicketLinkRef {
  id: number;
  org: string | null;
  project: string | null;
}

/**
 * One finished ticket review.
 *
 * `criteria` and `coverage` are what the sidecar parsed out of the model's answer with
 * `TicketVerdict`, stored as parsed so a later parser change cannot rewrite history. `review_md` is
 * the whole answer, findings and verdict sections together — `splitTicketReview` cuts it for the two
 * renderers.
 */
export interface TicketReviewResult {
  id: string;
  project_id: string;
  ticket_id: string;
  branch: string;
  base_ref: string;
  head_sha: string;
  level: string;
  review_md: string;
  criteria: TicketCriterionVerdictWire[];
  coverage: TicketCoverageWire | null;
  created_at: string;
}

/** The wire form of one criterion's verdict — snake_case, unlike `parseTicketVerdict`'s own type. */
export interface TicketCriterionVerdictWire {
  id: string;
  criterion: string;
  verdict: string;
  evidence: string;
  confidence: number | null;
}

/** The wire form of the coverage block. */
export interface TicketCoverageWire {
  coverage: string;
  missing: string;
  out_of_scope: string;
  summary: string;
}
