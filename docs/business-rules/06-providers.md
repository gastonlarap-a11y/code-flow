# 06 — VCS providers

## Scope

- `src/CodeFlow.App/Providers/GitHub/` — `GitHubClient.cs`, `GitHubHost.cs`, `GitHubModels.cs`
- `src/CodeFlow.App/Providers/Azure/` — `AzureClient.cs`, `AzureHost.cs`, `AzureModels.cs`, `UnifiedPatch.cs`
- `src/CodeFlow.App/Providers/PrLink.cs`, `RepoDetection.cs`, `LinkedRepo.cs`, `KnownHosts.cs`
- `src/CodeFlow.App/Providers/IPullRequestHost.cs` — the one interface over both hosts

This document owns the two provider REST/GraphQL clients and the pure PR-link parser. It does
**not** own the PR-review pipeline that calls into them (`src/CodeFlow.App/Review/` —
`07-review-pipeline.md`), nor the command parameters and returns catalogued in
`01-ipc-surface.md`.

## Commands

Both commands are defined in `src/CodeFlow.App/Providers/ProviderCommands.cs`. Parameters and return types are not
restated here — see `01-ipc-surface.md`.

- `link_project_github` (`src/CodeFlow.App/Providers/ProviderCommands.cs`) — manually associates a project with a
  `host/owner/repo`, bypassing git-remote auto-detection, by writing straight to the `projects`
  table via `src/CodeFlow.App/Storage/` store. Makes no network call.
- `github_authenticated_user` (`src/CodeFlow.App/Providers/ProviderCommands.cs`, async) — loads the saved GitHub
  token for `host` from the OS keychain and calls `get_authenticated_user`. This is the
  one call site in this document's scope where a token is read from storage.

## Shared concepts

**Provider-neutral wire types live in `src/CodeFlow.App/Providers/Azure/AzureClient.cs`, not a neutral module.** `PullRequestSummary`,
`PrCommentThread` and `PrThreadComment` are all defined in `src/CodeFlow.App/Providers/Azure/AzureClient.cs` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`,
`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) and re-exported into `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) with a comment explaining the
choice: "they're what the frontend already consumes, so GitHub produces the exact same shapes
rather than a parallel set the UI would have to learn." GitHub has no types of its own for a PR
summary or a comment thread — every GitHub function in this document builds an ADO-owned struct.

**Status bucketing.** Both providers collapse their own native status vocabulary into the same
four buckets the sidebar groups by — `"open" | "draft" | "merged" | "closed"` — via a
provider-local `bucket_status` function (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`). See PROV-007 and
PROV-027.

**Diff without a clone.** Both providers can produce a full unified diff for a PR with no local
git clone, which is what makes "review from a pasted link" possible — but by structurally
different means, because Azure DevOps has no endpoint that returns a diff as text:
- GitHub: one `Accept: application/vnd.github.diff` GET returns the literal diff GitHub itself
  would show; a per-file-hunk reassembly is the fallback only when GitHub declines to render it
  whole (PROV-010).
- Azure DevOps: there is no diff endpoint at all. `src/CodeFlow.App/Providers/Azure/AzureClient.cs` fetches each changed file's two blobs
  (old/new object id) and renders the unified diff itself with `git2::a blob-to-blob patch
  (libgit2) — the same library the rest of the app diffs with (PROV-032).

**Anchored-comment path normalization diverges in opposite directions.** Both providers require
a comment's target file path with a specific leading-slash convention, and both normalize
whatever the caller passed — but to opposite conventions, each matching its own API:
- GitHub's `post_pr_comment_anchored` strips a leading slash (`file_path.trim_start_matches('/')`,
  `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) — GitHub's REST API wants a bare repo-relative path.
- Azure's `post_pr_comment_anchored` adds a leading slash if missing
  (`if file_path.starts_with('/') { file_path.to_string() } else { format!("/{file_path}") }`,
  `src/CodeFlow.App/Providers/Azure/AzureClient.cs`) — Azure's `threadContext.filePath` wants an absolute-from-repo-root path.

Given the same input `"src/app.ts"`, GitHub sends `"src/app.ts"` and Azure sends `"/src/app.ts"`.
Given `"/src/app.ts"`, GitHub sends `"src/app.ts"` and Azure sends `"/src/app.ts"` unchanged. This
is deliberate, not an inconsistency to unify — each side is normalizing toward what its own
provider needs.

**Reviewer votes vs. review events are deliberately not unified.** GitHub models a PR decision as
a sequence of *review events* (`APPROVED`, `CHANGES_REQUESTED`, `DISMISSED`, `COMMENTED`,
`PENDING`), keyed off whichever user submitted them (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). Azure DevOps models it
as a single *numeric vote* per reviewer, stored as a field on the PR's reviewer list: `10`
approved, `5` approved with suggestions, `0` no vote, `-5` waiting for author, `-10` rejected
(`src/CodeFlow.App/Providers/Azure/AzureClient.cs`, and cast via `set_reviewer_vote`, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Each provider's
`viewer_decision` independently collapses its own model into the same three-way
`"approved" | "changes_requested" | "none"` string the frontend consumes — but nothing in this
codebase translates one provider's raw verdict shape into the other's, and no shared enum exists
above the string level. `DIVERGENCE-PROV-a`: preserve this — do not introduce a unified vote/event
type in the port.

**Structural difference, not behavioral.** `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` centralizes its HTTP verbs into four
helpers — `get_json`, `post_json`, `post_json_returning`, `patch_json`
(`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) — each attaching the same four headers and doing the same error mapping.
`src/CodeFlow.App/Providers/Azure/AzureClient.cs` mostly inlines the `reqwest` call in every function and only factors out one shared
helper, `post_thread` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), used by both of its comment-posting functions. This is a
code-organization difference with no behavioral consequence, noted so the port doesn't read
significance into it.

**Two independent "detect a PR" paths that deliberately don't share code.** `detect_from_remote_url` /
`detect_from_remote_url` parse a **git remote URL** (what a local clone reports); `src/CodeFlow.App/Providers/PrLink.cs`::parse`
parses a **pasted browser URL** (what a human copies from the PR page). `src/CodeFlow.App/Providers/PrLink.cs`'s own module
doc comment (`src/CodeFlow.App/Providers/PrLink.cs`) states this is deliberate: "this is deliberately the mirror image
of `detect_from_remote_url` / `detect_from_remote_url`... Nothing here talks to a
network." The two paths use different decoders (`src/CodeFlow.App/Providers/PrLink.cs`'s `percent_decode` is a full
percent-decoder; `src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s `decode_path_segment` only understands `%20` — see `BUG-PROV-b`) and
are not merged in the port.

## GitHub

Const `API_VERSION = "2022-11-28"` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) is sent as the `X-GitHub-Api-Version` header
on every REST call, pinning behavior to a dated snapshot instead of "latest". Const
`USER_AGENT = "CodeFlow"` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) is sent on every REST **and** GraphQL call — GitHub
rejects any request with no `User-Agent` with a 403, which Azure DevOps does not require.

**Host resolution** (`api_root`, `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`; `graphql_root`, `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`):
`host.eq_ignore_ascii_case("github.com")` selects `https://api.github.com` (REST) /
`https://api.github.com/graphql` (GraphQL); any other host is treated as a GitHub Enterprise
Server and gets `https://{host}/api/v3` (REST) / `https://{host}/api/graphql` (GraphQL).

**Auth.** Every request carries `Authorization: Bearer {token}` (`bearer()`, `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) —
one scheme for both classic and fine-grained personal access tokens, per the function's doc
comment. The token is loaded from the OS keychain by the caller and passed in as a plain `string`
parameter to every function in `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`; within this file the token is used **only** inside the
`Authorization` header of an outbound request — it is never written into a diff, a comment body,
or any other payload these functions construct, and there is no code path in `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` that
forwards it anywhere else. The one call site visible in this document's scope that reads the token
from storage is `src/CodeFlow.App/Providers/ProviderCommands.cs` — `CredentialStore.Get`(&`CredentialStore.GitHubTokenKey`(&host))`
— the storage format itself (`src/CodeFlow.App/Security/CredentialStore.cs`) is owned by another document.

**Pagination.** Every list endpoint below states its own pagination (or lack of it) explicitly;
there is no shared pager.

**Error mapping.** Every REST call funnels through one of `get_json` / `post_json` /
`post_json_returning` / `patch_json` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). A transport failure (DNS, TLS, refused
connection, timeout) becomes `"couldn't reach GitHub: {e}"`; any non-2xx response becomes
`"GitHub returned {status}: {body}"` with `body` being the raw response text (JSON error object or
otherwise) — **no branch distinguishes status codes**, so a 401 (bad/expired token), a 403 (scope
missing), a 404 (repo/PR not found) and a 422 (validation) all produce the same string shape,
differing only in the interpolated status/body. A response body that fails to deserialize into the
expected type becomes `"unexpected response from GitHub: {e}"`.

> **`DIVERGENCE-PROV-c` — the port branches on exactly one of these.** The 422 GitHub returns for
> approving one's own pull request now sets `GitHubException.SelfApproval` and, at the
> `act_on_pull_request` / `act_on_pr_link` boundaries, carries the `SELF_APPROVAL: ` sentinel
> (`XLANG-013`); the panel disables Approve on a pull request whose author is the signed-in login, so
> the error is a backstop rather than the normal path. **Nothing else changed** — a 401 is still an
> undifferentiated 401, and a 422 that is not this one is still a raw 422, both asserted by their own
> tests. The operator asked for this after meeting the raw JSON in a toast; it is not a silent
> correction. See `90-ambiguities.md`.

### REST endpoints

- **`GET {api_root}/user`** — `get_authenticated_user` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). No query params.
  Response consumed: `{ login: string }`. Used to validate a pasted token and show whose account
  it belongs to.
- **`GET {api_root}/repos/{owner}/{repo}/pulls?state=all&per_page=100&sort=created&direction=desc`**
  — `list_pull_requests` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). Fixed at 100 results, **no further pagination** —
  a repo with more than 100 PRs across all states will silently lose whichever fall past the first
  page (the newest 100 by creation date are kept, since `sort=created&direction=desc`). Response
  consumed per item: `number, title, body?, state, draft?, merged_at?, head.ref, head.sha, base.ref,
  user.login, created_at, html_url` (`RawPull`, `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), mapped through `bucket_status`
  (below) and `map_pull` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) into `PullRequestSummary`.
- **`GET {api_root}/repos/{owner}/{repo}/pulls/{number}`** — `get_pull_request`
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`); also reused verbatim by `head_sha_for` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), which reads
  only `head.sha` and errors `"GitHub didn't report a head commit for this pull request"` if it is
  empty. No query params.
- **`GET {api_root}/repos/{owner}/{repo}/pulls/{number}`** with header
  **`Accept: application/vnd.github.diff`** — `pull_request_diff`'s primary path
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). If the response is non-2xx, or 2xx with an empty/whitespace-only body,
  falls back to `pull_request_diff_from_files` (below) rather than erroring — the empty-body case
  is GitHub declining to render a diff past its internal size limit.
- **`GET {api_root}/repos/{owner}/{repo}/pulls/{number}/files?per_page=100&page={1,2,3}`** —
  `pull_request_diff_from_files` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), the fallback. Pages 1–3 only (300 files
  total — the comment at `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` states this is "GitHub's own hard ceiling for this
  endpoint"; past it GitHub itself truncates). Response consumed per file: `filename,
  previous_filename?, status, patch?` (`RawPullFile`, `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). Reassembles a unified
  diff by hand:
  - `status == "added"` → old path is `/dev/null`; otherwise old path is
    `a/{previous_filename or filename}`.
  - `status == "removed"` → new path is `/dev/null`; otherwise new path is `b/{filename}`.
  - Header line: `diff --git a/{previous_filename or filename} b/{filename}\n--- {old}\n+++ {new}\n`.
  - Body: `patch` verbatim (with a trailing `\n` appended if missing) when present; when `patch` is
    `None` (binary file, or one whose diff GitHub judged too large to inline), the literal line
    `"(binary or too large to display)\n"` instead.
  - If zero files came back across all pages, the whole call errors:
    `"GitHub reported no changed files for this pull request"`.
- **`POST {api_root}/repos/{owner}/{repo}/pulls`** — `create_pull_request`
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). Body: `{ title, head, base, body, draft }` where `head`/`base` are branch
  names (not full refs) and the branch must already exist on the remote. Response consumed: the
  same `RawPull` shape as the GET, mapped through `map_pull`.
- **`POST {api_root}/repos/{owner}/{repo}/pulls/{pr_number}/comments`** — `post_pr_comment_anchored`
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), **`VERIFIED-LIVE`** (§2.9, 2026-08-01 — four anchored comments landed
  on a real PR with correct `path` + `start_line`/`line` ranges, confirmed via the REST API from
  outside the app). Body:
  `{ body: content, commit_id, path, line, side: "RIGHT" }`, plus `start_line` and
  `start_side: "RIGHT"` **only when the range spans more than one line**
  (`line = end_line.max(start_line)`; `start_line`/`start_side` set only if `start_line < line`) —
  the comment at `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` notes GitHub 422s a single-line comment that also carries
  `start_line == line`. `path` has its leading slash stripped (see Shared concepts). `commit_id`
  is the PR's current head SHA, fetched fresh via `head_sha_for` immediately before posting so a
  re-review still anchors to the tip. Response consumed: `{ id }` (`CommentCreated`,
  `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) — the comment id is returned so a later re-review can reply to it or resolve
  its thread.
- **`POST {api_root}/repos/{owner}/{repo}/issues/{pr_number}/comments`** — `post_pr_comment`
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), **`VERIFIED-LIVE`** (§2.9, 2026-08-01 — the summary landed as an
  unanchored issue comment). Body: `{ body: content }`. GitHub models a PR as
  an issue for conversation-level comments, hence the `/issues/` path rather than `/pulls/`.
  Response consumed: `{ id }` — returned but not reused (issue comments aren't threaded).
- **`POST {api_root}/repos/{owner}/{repo}/pulls/{pr_number}/comments/{comment_id}/replies`** —
  `reply_pr_review_comment` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), **`VERIFIED-LIVE`** (§2.9, 2026-08-01 —
  re-publishing a still-open finding replied into its saved thread; every reply landed under its
  root, none opened a new thread). Body:
  `{ body: content }`. Threads the reply off the root comment's id — the comment id kept from an
  earlier `post_pr_comment_anchored` call.
- **`GET {api_root}/repos/{owner}/{repo}/pulls/{number}/reviews?per_page=100`** —
  `viewer_decision` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). No further pagination (100-item cap, same limitation
  pattern as `list_pull_requests`). First calls `get_authenticated_user` to know which login is
  "the viewer". Iterates **every** review by that login, in whatever order GitHub returns them
  (undocumented in the source; assumed chronological), and lets the last one that carries a verdict
  win: `APPROVED` → `"approved"`, `CHANGES_REQUESTED` → `"changes_requested"`, `DISMISSED` →
  `"none"` (a dismissal takes back a prior verdict); `COMMENTED` and `PENDING` leave the running
  decision unchanged. Response consumed per item: `{ user: { login }, state }` (`RawReview`,
  `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`).
- **`POST {api_root}/repos/{owner}/{repo}/pulls/{pr_number}/reviews`** — `submit_pr_review`
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`), **`VERIFIED-LIVE`** (§2.9, 2026-08-01 — executed for real; what came
  back was the 422 self-approval, classified as `SELF_APPROVAL: ` exactly as `XLANG-013`
  promises. A 2xx APPROVE remains unexercised, structurally: the token owner authored the
  throwaway PR, and GitHub refuses self-approval — verifying the 2xx needs a second account).
  Body: `{ event }` where `event` is GitHub's verb
  (`"APPROVE"` / `"REQUEST_CHANGES"`), plus `body: content` **only when `content.trim()` is
  non-empty** — an approval can carry no comment, but the field is omitted rather than sent empty.
  Reviewer identity is inferred from the token; no user-id lookup.
- **`PATCH {api_root}/repos/{owner}/{repo}/pulls/{pr_number}`** — `close_pull_request`
  (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). Body: `{ state: "closed" }`. This is a close, not a merge.
- **`GET {api_root}/repos/{owner}/{repo}/pulls/{pr_number}/comments?per_page=100`** and
  **`GET {api_root}/repos/{owner}/{repo}/issues/{pr_number}/comments?per_page=100`** —
  `list_pr_comment_threads` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`). No further pagination on either call. See
  PROV-020 below for the grouping algorithm.

### The GraphQL call

`resolve_review_thread_for_comment` (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`) — **`VERIFIED-LIVE`** (§2.9,
2026-08-01 — publishing items matching two stored `resuelto` findings replied a follow-up and
resolved both threads; `isResolved: true` confirmed via GraphQL from outside the app). There is no REST endpoint
to resolve a review thread; the doc comment at `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` states this directly: "There's
no REST endpoint for it, so it goes through GraphQL." Endpoint: `POST {graphql_root}` with header
`Authorization: Bearer {token}` and `User-Agent` (no `Accept` / no `X-GitHub-Api-Version` — those
are REST-only headers and are not sent on this call). Two sequential requests:

1. Find the review-thread node id that owns `comment_id`. Query, `VERBATIM` (the the sidecar `format!`
   template — `{owner}`, `{repo}`, `{pr_number}` are interpolated, unescaped, directly from the
   caller's arguments):
   `graphql
   query { repository(owner: "{owner}", name: "{repo}") { pullRequest(number: {pr_number}) { reviewThreads(first: 100) { nodes { id isResolved comments(first: 100) { nodes { databaseId } } } } } } }
   `
   Capped at the first 100 review threads and, within each, the first 100 comments — a PR past
   either cap could have a thread this call cannot find. Response walked as raw
   `JsonElement`: `data.repository.pullRequest.reviewThreads.nodes[]`, each with
   `comments.nodes[].databaseId` compared against `comment_id`; the first thread whose comments
   contain it is selected by its `id` (the GraphQL node id, a string — distinct from `databaseId`,
   the REST comment id). Errors: `"couldn't reach GitHub: {e}"` (transport), `"GitHub GraphQL
   returned {status}"` (non-2xx — note this omits the response body, unlike every REST error
   string in this file), `"no review threads in GraphQL response"` (the `nodes` array is missing
   from the shape the code expects), `"couldn't find the review thread for this comment"` (no
   thread's comments contained `comment_id`).
2. Resolve it. Mutation, `VERBATIM`:
   `graphql
   mutation { resolveReviewThread(input: { threadId: "{thread_id}" }) { thread { isResolved } } }
   `
   `thread_id` is the node id found in step 1. Non-2xx: `"GitHub GraphQL resolve returned
   {status}"` (again no body). Success is not otherwise verified (the returned `isResolved` is not
   checked).

The call site treats this as best-effort by design (per the doc comment at `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`):
"a failure just leaves the thread open, it never fails the post" — that call site is in
`src/CodeFlow.App/Review/ReviewCommands.cs`, out of this document's scope.

## Azure DevOps

Const `API_VERSION = "7.1"` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) is appended as `?api-version=7.1` (or joined with `&`) to
every REST call except `connectionData`, which uses `PREVIEW_API_VERSION = "7.1-preview"`
(`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) — the doc comment states this endpoint "never went GA, and the server rejects a
plain `7.1` on them with a 400 demanding the `-preview` suffix."

**Auth.** `auth_header` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`): `Authorization: Basic {base64(":" + pat)}` — an empty
username, the PAT as the password, standard HTTP Basic construction
(`base64::general_purpose.STANDARD.encode(format!(":{pat}"))`). Every function in
`src/CodeFlow.App/Providers/Azure/AzureClient.cs` takes `pat: string` as an explicit parameter — there is no ambient/global credential. Where
the PAT is loaded from storage is out of this document's scope (no `CredentialStore` call appears in
`src/CodeFlow.App/Providers/Azure/AzureClient.cs` or in this document's other files); within `src/CodeFlow.App/Providers/Azure/AzureClient.cs` the PAT is used **only** inside this
header, never embedded in a request body, diff text or comment content.

**Organization scoping.** Every exported function in `src/CodeFlow.App/Providers/Azure/AzureClient.cs` — `list_projects`, `list_repos`,
`list_pull_requests`, `get_pull_request`, `create_pull_request`, `pull_request_diff`,
`post_pr_comment_anchored`, `post_pr_comment`, `reply_pr_thread`, `set_pr_thread_status`,
`set_reviewer_vote`, `viewer_decision`, `abandon_pull_request`, `list_pr_comment_threads` — takes
`org: string` as an explicit, required parameter; there is no ADO call in this file that can be made
without naming which organization's PAT to use. This matches the brief's framing of ADO PATs as
organization-scoped at the credential level. The credential record's own shape (whether
`organization` is a stored field, how it's keyed) is not visible in this document's files — that
belongs to the storage/secrets document.

**Token-expiry detection is not visible in these files.** `AMBIGUOUS-PROV-b`: every non-2xx
response in `src/CodeFlow.App/Providers/Azure/AzureClient.cs`, from every endpoint, is converted by the same uniform pattern — a 401 from
an expired/revoked PAT produces exactly the same shape as a 404 or a 422, differing only in the
interpolated status code and body text (`format!("Azure DevOps returned {status}: {body}")`, e.g.
`src/CodeFlow.App/Providers/Azure/AzureClient.cs`, `:374-376`, `:455-456`, `:651-653`, `:685-687`, `:718-720`, `:775-776`, `:840-841`).
There is no branch anywhere in `src/CodeFlow.App/Providers/Azure/AzureClient.cs` that inspects the status code and produces a distinct
"expired" error, and no dedicated expiry-check function. Whatever UI path shows PAT expiry as a
distinct state (rather than a generic network error) must therefore live either in
`src/CodeFlow.App/Review/ReviewCommands.cs` or in the secrets/storage layer, both out of this document's scope — this
document cannot establish *how* expiry is detected and surfaced, only that it is not decided in
`src/CodeFlow.App/Providers/Azure/AzureClient.cs` itself.

**Auth header is sent even on the org-scoped `connectionData` preview call and on blob reads** —
there is no endpoint in this file that omits the `Authorization` header.

**Error mapping.** Near-uniform: `"couldn't reach Azure DevOps: {e}"` for a transport failure,
`"Azure DevOps returned {status}: {body}"` for any non-success response, `"unexpected response
from Azure DevOps: {e}"` for a body that fails to deserialize (`get_json`, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`). One
exception: `get_blob` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) uses a shorter string that drops the response body —
`"Azure DevOps returned {status} reading a file"` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) — the only error string in this
file that doesn't carry the server's response text.

### Path-segment percent-encoding

`normalize_org` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) accepts whatever the user saved as "organization" and reduces it
to the bare name: strips a `https://dev.azure.com/` or `http://dev.azure.com/` prefix and takes
the first remaining path segment; otherwise strips `https://`/`http://` and, if the host ends in
`.visualstudio.com`, returns the subdomain; otherwise returns the trimmed input as-is. The doc
comment states the reason normalization exists at all: "Azure DevOps' server rejects any literal
`:` in the request path (IIS request validation)," so a raw URL interpolated into a path segment
would 400/404 in a confusing way.

`encode_segment` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) percent-encodes a single path segment byte-by-byte: bytes in
`A-Za-z0-9-._~` pass through unchanged; every other byte becomes `%{byte:02X}`. Applied
consistently to `org` (after `normalize_org`) and to `project` at every call site in this file.
**`BUG-PROV-a`**: it is **not** applied consistently to `repo_id`. `get_pull_request`
(`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), `viewer_decision` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), and `pull_request_diff`'s blob/changes
URLs (via `repo_enc = encode_segment(repo_id)`, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`) all encode it; `list_pull_requests`
(`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), `create_pull_request` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), `get_latest_iteration_id`
(`src/CodeFlow.App/Providers/Azure/AzureClient.cs`, which `post_pr_comment_anchored` calls internally), `post_pr_comment_anchored`'s own
thread URL (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), `post_pr_comment` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), `reply_pr_thread` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`),
`set_pr_thread_status` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), `set_reviewer_vote` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), and
`abandon_pull_request` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) all interpolate `repo_id` **raw**, unencoded, straight into
the URL path. Since `DetectedAdoRepo.repo` documents that "Azure DevOps' Git REST API accepts
either the repository's GUID or its plain name" (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), a repository referenced by its
plain **name** containing a space or other reserved character would be percent-encoded on the
minority of calls and sent raw on the majority. Suspected-correct behavior: `encode_segment(repo_id)`
applied uniformly everywhere `repo_id` is interpolated by name, matching how `org`/`project` are
always treated. Ported as-is, inconsistency included — not fixed here.

### REST endpoints

- **`GET https://dev.azure.com/{org}/_apis/projects?api-version=7.1`** — `list_projects`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). No further pagination. Response: `{ value: [{ id, name }] }`
  (`ListResponse<AdoProject>`).
- **`GET https://dev.azure.com/{org}/{project}/_apis/git/repositories?api-version=7.1`** —
  `list_repos` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Response: `{ value: [{ id, name }] }` (`ListResponse<AdoRepo>`).
- **`GET https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo_id}/pullrequests?searchCriteria.status=all&api-version=7.1`**
  — `list_pull_requests` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). `searchCriteria.status=all` covers active/completed/
  abandoned in one call — no explicit page size or pagination is set (unlike GitHub's
  `per_page=100`), so whatever the server's own default page size is applies uncontrolled; this
  document cannot establish a numeric cap from the source. Response items mapped via
  `map_pull_request` + `bucket_status`: `"completed"` → `"merged"`, `"abandoned"` → `"closed"`,
  else `is_draft` → `"draft"`, else `"open"` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). `PullRequestSummary.url` is
  synthesized (not read from the API): `https://dev.azure.com/{org_enc}/{project_enc}/_git/{repo_name_enc}/pullrequest/{id}`.
- **`GET https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo_id}/pullRequests/{pr_id}?api-version=7.1`**
  — `get_pull_request` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). `project`/`repo_id` may each be a GUID or a name.
  Additionally recovers the **project's name** (not just its id) from `repository.project.name` on
  the response (`RawRepoRef`, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`) — needed because a PR reached via a GUID-carrying
  link (e.g. Azure's own notification e-mails) has no name to match against a local clone's git
  remote, which only ever spells out names; falls back to the `project` argument as given if the
  API didn't report one. Returns `AdoPullRequest { summary, project_name, repo_name }`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`).
- **`POST https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo_id}/pullrequests?api-version=7.1`**
  — `create_pull_request` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Body:
  `{ sourceRefName: "refs/heads/{source_branch}", targetRefName: "refs/heads/{target_branch}", title, description, isDraft: draft }`
  — Azure requires the full `refs/heads/` prefix (inverse of `strip_ref`, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`, which
  strips it on the way out). Response: the same `RawPullRequest` shape as the GET.
- **`GET .../pullRequests/{pr_id}/iterations?api-version=7.1`** — `get_latest_iteration_id`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Response: `{ value: [{ id }] }`; takes `.last()`'s id, or **falls back to
  `1`** if the list is empty ("shouldn't happen for a real PR, but a comment landing on iteration 1
  beats the whole review failing to post," `src/CodeFlow.App/Providers/Azure/AzureClient.cs`).
- **`GET .../repositories/{repo_id}/blobs/{sha}?api-version=7.1`** with header
  `Accept: application/octet-stream` — `get_blob` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Returns raw bytes. Its error
  string omits the response body (noted above).
- **`GET .../pullRequests/{pr_id}/iterations/{iteration_id}/changes?$top=1000&api-version=7.1`** —
  `pull_request_diff` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). No `$compareTo`, so changes are measured against the
  base of the whole PR, not just the last push. Response:
  `{ changeEntries: [{ changeType, item: { path, objectId, originalObjectId, isFolder } }] }`.
  See PROV-032 (Rules) for the full diff-assembly algorithm.
- **`POST .../pullRequests/{pr_id}/threads?api-version=7.1`** — shared by
  `post_pr_comment_anchored` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) and `post_pr_comment` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) via
  `post_thread` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), both **`VERIFIED-LIVE`** (§2.9, 2026-08-01 — four
  anchored threads with correct `threadContext.filePath` + right-file line ranges, plus one
  unanchored summary thread, confirmed via the REST API from outside the app). See PROV-033/034
  (Rules) for the body shapes.
- **`POST .../pullRequests/{pr_id}/threads/{thread_id}/comments?api-version=7.1`** —
  `reply_pr_thread` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), **`VERIFIED-LIVE`** (§2.9, 2026-08-01 — replies
  landed inside their saved threads; the hardcoded `parentCommentId: 1` held on every thread this
  app created). Body:
  `{ parentCommentId: 1, content, commentType: 1 }` — `parentCommentId` is **hardcoded to `1`**,
  relying on Azure numbering each thread's own comments starting at 1 (the root comment of any
  thread this app created is always comment id 1 within that thread, since every thread created by
  `post_thread` starts with exactly one comment). This is not re-derived from the actual thread
  being replied to.
- **`PATCH .../pullRequests/{pr_id}/threads/{thread_id}?api-version=7.1`** —
  `set_pr_thread_status` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), **`UNVERIFIED`** (§2.9 — the one write path the
  2026-08-01 live run could not reach: it only fires when a re-review marks a posted finding
  `resuelto`, which needs the PR's remote source branch to change, and the throwaway Azure repo
  allowed exactly one push. Verifiable later with any disposable ADO repo that allows a second
  push). Body: `{ status }`. Status
  ints, `VERBATIM` from the doc comment at `src/CodeFlow.App/Providers/Azure/AzureClient.cs`: `1`=active, `2`=fixed, `3`=wontFix,
  `4`=closed, `5`=byDesign, `6`=pending. A resolved finding's thread is set to `2`.
- **`GET https://dev.azure.com/{org}/_apis/connectionData?api-version=7.1-preview`** —
  `authenticated_user_id` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Org-scoped, **not** project- or repo-scoped. Response
  consumed: `{ authenticatedUser: { id } }` — the signed-in user's Azure DevOps GUID, needed
  because Azure votes are keyed by reviewer id rather than inferred from the token (unlike
  GitHub's reviews).
- **`PUT .../pullRequests/{pr_id}/reviewers/{user_id}?api-version=7.1`** — `set_reviewer_vote`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), **`VERIFIED-LIVE`** (§2.9, 2026-08-01 — an approve from the app added
  the caller as reviewer with vote `10` in one call; the vote read back both through
  `viewer_decision` and through the REST API from outside the app. Azure, unlike GitHub, accepts
  a vote on one's own PR). Body: `{ vote }` — `10`/`5`/`0`/`-5`/`-10` (see
  Shared concepts). PUT-ing to `reviewers/{id}` both adds the user as a reviewer (if not already
  one) and sets their vote in one call. Calls `authenticated_user_id` first.
- **`GET .../pullRequests/{pr_id}?api-version=7.1`** — `viewer_decision` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`), same
  URL shape as `get_pull_request` (single-PR read includes `reviewers`, which the list endpoint
  omits — `RawPullRequest.reviewers` defaults to empty, `src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Finds the entry whose
  `id` matches the authenticated user's (case-insensitive), reads its `vote`: `> 0` → `"approved"`,
  `< 0` → `"changes_requested"`, `0` or absent → `"none"` — this collapses vote `5`
  (approve-with-suggestions) into the same `"approved"` bucket as `10`, and `-5`
  (waiting-for-author) into the same `"changes_requested"` bucket as `-10`.
- **`PATCH .../pullRequests/{pr_id}?api-version=7.1`** — `abandon_pull_request`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). Body: `{ status: "abandoned" }`. Azure's equivalent of closing without
  merging.
- **`GET .../pullRequests/{pr_id}/threads?api-version=7.1`** — `list_pr_comment_threads`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`). See PROV-042 (Rules) for the filtering algorithm.

### Comment-thread positioning (`threadContext`)

`post_pr_comment_anchored`'s body (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`):
`json
{
  "comments": [{ "parentCommentId": 0, "content": "...", "commentType": 1 }],
  "status": 1,
  "threadContext": {
    "filePath": "/src/app.ts",
    "rightFileStart": { "line": 12, "offset": 1 },
    "rightFileEnd": { "line": 14, "offset": 1 }
  },
  "pullRequestThreadContext": {
    "iterationContext": { "firstComparingIteration": 1, "secondComparingIteration": 7 }
  }
}
`
- `filePath` normalization: `if file_path.starts_with('/') { file_path.to_string() } else { format!("/{file_path}") }`
  (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`) — a leading slash is added when missing, left unchanged when already present.
  Never stripped, unlike GitHub's normalizer.
- `rightFileStart.line` = `start_line`, `rightFileEnd.line` = `end_line.max(start_line)` — both
  always carry `offset: 1` (column 1; the app never anchors to a specific column).
- `iterationContext.secondComparingIteration` is the PR's latest iteration id (from
  `get_latest_iteration_id`); `firstComparingIteration` is hardcoded to `1`.
- `status: 1` (active) and `parentCommentId: 0` (root comment, no parent) are constant for every
  new thread.

## PR link parsing

`src/CodeFlow.App/Providers/PrLink.cs` is pure — no network, no filesystem — parsing a **browser** URL (as opposed to
`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`/`src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s remote-URL detectors, which parse a **git remote**). Full behavior is
captured as fixtures in `test-vectors/pr_link.vectors.json`; this section states the grammar.

**Percent-decoding** (`percent_decode`, `src/CodeFlow.App/Providers/PrLink.cs`): a hand-rolled decoder, not
`the url library based. For each byte: if it's `%` and there are at least two more bytes remaining,
and both are valid hex digits, emit the decoded byte and advance 3; otherwise emit the byte
literally and advance 1. A `%` not followed by two hex digits (truncated at the end of the string,
or followed by non-hex characters) is left in the output as a literal `%`, per the doc comment:
"A stray `%` that isn't a valid escape is left as-is rather than dropped." Decoded via
lossy UTF-8 decoding — invalid UTF-8 in the decoded bytes is replaced, not rejected.

**Splitting** (`split`, `src/CodeFlow.App/Providers/PrLink.cs`): strips everything from the first `#` onward, then
everything from the first `?` onward, then a trailing `/`; strips an optional `https://`/`http://`
scheme; strips `user@` credentials (`rsplit('@')`, takes the last segment — so a `@` inside a
later path segment is not mistaken for userinfo); splits host from path on the first `/`; returns
`None` if there's no `/` at all, or the host half is empty. Remaining path segments are split on
`/`, empty segments dropped, and each percent-decoded.

**GitHub grammar** (`parse_github`, `src/CodeFlow.App/Providers/PrLink.cs`): `host` must case-insensitively match an
entry in `known_github_hosts`. Segments must destructure as `[owner, repo, kind, number, ..]`
(trailing segments — `/files`, `/commits`, etc. — ignored); `kind` must case-insensitively be
`"pull"` or `"pulls"`; `repo` has a trailing `.git` stripped; `number` must parse as `long`. Host in
the result is the **matched `known_github_hosts` entry** (canonical casing), not the input host
string.

**Azure grammar** (`parse_azure`, `src/CodeFlow.App/Providers/PrLink.cs`): if `host` is `dev.azure.com`
(case-sensitive-normalized via `to_ascii_lowercase` comparison), `org` is the first path segment
and the rest is matched against `[project, "_git", repo, "pullrequest"|"pullrequests", number, ..]`
or, when the project segment is absent, `[".._git", repo, "pullrequest"|"pullrequests", number, ..]`
with `project` defaulting to `repo`'s value ("Azure drops it when it matches the repository
name"). If `host` ends in `.visualstudio.com`, `org` is the subdomain and the same two-shape match
runs against the full segment list, first stripping a leading `"DefaultCollection"` segment
(case-insensitive) if present. `"_git"` and `"pullrequest"/"pullrequests"` are matched
case-insensitively; the PR number must parse as `long`.

**Dispatch** (`parse`, `src/CodeFlow.App/Providers/PrLink.cs`): tries `parse_github` first, then `parse_azure`;
first `Some` wins. A URL that doesn't parse as a browser URL at all (`split` returns `None`)
short-circuits to `None` before either provider is tried.

**Rejected shapes** (see the `reject-*` fixture cases): a repo-root link with no PR segment; a
GitHub `/issues/{n}` link (wrong `kind`); an Azure repo-browse link with no `/pullrequest/{n}`
tail; a string with no `/` at all.

## Rules

### PROV-001 GitHub REST/GraphQL host resolution
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `api_root(host)` returns `https://api.github.com` when `host` case-insensitively
equals `"github.com"`, else `https://{host}/api/v3`. `graphql_root(host)` returns
`https://api.github.com/graphql` for `github.com`, else `https://{host}/api/graphql`.
**Inputs / outputs**: `host: string` → base URL `string`.
**Edge cases**: any non-`github.com` host, including a typo'd host, is treated as a valid GitHub
Enterprise Server — there is no validation that the host is actually reachable or actually GitHub
here (that's what `detect_from_remote_url`'s `known_hosts` allowlist is for, at a different layer).
**Frontend dependency**: none directly — used by every GitHub REST/GraphQL call in this file.
**Markers**: none.

### PROV-002 GitHub Bearer auth
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`, applied at every call site in `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: every GitHub request carries `Authorization: Bearer {token}`. One scheme for both
classic and fine-grained PATs.
**Inputs / outputs**: `token: string` → header value `string`.
**Edge cases**: no validation of token shape/format before sending.
**Frontend dependency**: none — see `src/CodeFlow.App/Providers/ProviderCommands.cs` for the one visible call site
that loads a token from storage.
**Markers**: none.

### PROV-003 GitHub git-remote detection
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `detect_from_remote_url(remote_url, known_hosts)` splits a git remote into
`(host, path)` (`split_host_path`, handling `scheme://[user@]host/path` and scp-like
`[user@]host:path`, stripping `.git` and a trailing `/`), requires `host` to case-insensitively
match an entry in `known_hosts`, then takes the first two non-empty `/`-separated segments of
`path` as `(owner, repo)` (`two_segments`, ignoring anything deeper, stripping a trailing `.git`
from `repo`). Returns the **matched `known_hosts` entry**, not the raw input host.
**Inputs / outputs**: `remote_url: string`, `known_hosts: IReadOnlyList<string>` → `DetectedGithubRepo{host,owner,repo}?`.
**Edge cases**: a host not in `known_hosts` (including a real GitHub Enterprise host the user
hasn't connected) returns `None` and falls back to manual linking. A path with fewer than two
segments returns `None`.
**Frontend dependency**: not established in this document's scope — see `01-ipc-surface.md`.
**Markers**: none.

### PROV-004 GitHub generic error mapping
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `get_json`/`post_json`/`post_json_returning`/`patch_json` each send the same four
headers (`Authorization`, `Accept: application/vnd.github+json`, `User-Agent: CodeFlow`,
`X-GitHub-Api-Version: 2022-11-28`); a transport error becomes `"couldn't reach GitHub: {e}"`; any
non-2xx becomes `"GitHub returned {status}: {body}"` (`body` = raw response text); a
deserialization failure on an otherwise-2xx body becomes `"unexpected response from GitHub: {e}"`.
**Inputs / outputs**: url/token/(body) → `T` or `void`.
**Edge cases**: no status-code-specific branching anywhere — 401/403/404/422 all produce the same
string shape.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-005 GitHub authenticated-user lookup
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `GET {api_root}/user`; returns the `login` of the token's owner.
**Inputs / outputs**: `host, token` → `string` (the login).
**Edge cases**: none beyond the generic error mapping.
**Frontend dependency**: `src/CodeFlow.App/Providers/ProviderCommands.cs` (`github_authenticated_user`); see `01-ipc-surface.md`.
**Markers**: none.

### PROV-006 GitHub status bucketing
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `bucket_status(state, draft, merged_at)`: `merged_at.is_some()` → `"merged"`; else
`state == "closed"` → `"closed"`; else `draft` → `"draft"`; else `"open"`. Merge takes priority
over the raw `state` field.
**Inputs / outputs**: `state: string, draft: bool, mergedAt: string?` → one of four strings.
**Edge cases**: a PR that is both `state == "closed"` and has `merged_at` set is bucketed
`"merged"` (merge check runs first).
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-007 GitHub list/get pull request
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`, `237-250`
**Behaviour**: `list_pull_requests`: `GET .../pulls?state=all&per_page=100&sort=created&direction=desc`,
maps every item through `map_pull`. `get_pull_request`: `GET .../pulls/{number}`, same mapping —
reaches a PR regardless of how far back in the list it is.
**Inputs / outputs**: `host, owner, repo, (number,) token` → `IReadOnlyList|PullRequestSummary, string>`.
**Edge cases**: `list_pull_requests` is capped at 100 results with no further pagination — a repo
with more than 100 all-time PRs loses whichever fall outside the newest 100 by creation date.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-008 GitHub diff retrieval and fallback reconstruction
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `pull_request_diff` requests `GET .../pulls/{number}` with
`Accept: application/vnd.github.diff`; if the response is non-2xx or the body is empty/whitespace
after success, falls back to `pull_request_diff_from_files`, which pages `GET
.../pulls/{number}/files?per_page=100&page={1..=3}` and reassembles `diff --git a/{old} b/{new}`
headers plus each file's `patch` field by hand (see the enumerated algorithm in the GitHub REST
section above).
**Inputs / outputs**: `host, owner, repo, number, token` → `string` (the unified
diff text).
**Edge cases**: a file with `patch: null` (binary, or over GitHub's inline-diff size limit)
contributes only its header line plus the literal text
`"(binary or too large to display)\n"` — no hunk. Zero changed files across all three pages errors
`"GitHub reported no changed files for this pull request"`.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-009 GitHub create pull request
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `POST .../pulls` with `{ title, head, base, body, draft }`; `head`/`base` are
branch names, not full refs; the branch must already exist on the remote. Response mapped through
`map_pull`.
**Inputs / outputs**: `host, owner, repo, title, body, head, base, draft, token` →
`PullRequestSummary`.
**Edge cases**: none beyond generic error mapping.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-010 GitHub head SHA lookup
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `GET .../pulls/{pr_number}`, reads only `head.sha`.
**Inputs / outputs**: `host, owner, repo, pr_number, token` → `string`.
**Edge cases**: an empty `head.sha` errors `"GitHub didn't report a head commit for this pull
request"` rather than returning the empty string.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-011 GitHub anchored inline comment
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `POST .../pulls/{pr_number}/comments` with
`{ body, commit_id, path, line, side: "RIGHT" }`; `line = end_line.max(start_line)`; `start_line`/
`start_side: "RIGHT"` added only when `start_line < line`. `path` has a leading `/` stripped.
Returns the created comment's `id`.
**A line the diff does not contain falls back to a conversation comment.** GitHub answers `422` when
any part of the anchor is outside the diff, and `GitHubHost` retries the same text unanchored,
prefixed with the file and lines it could not be attached to. Observed live: a **critical** finding
cited lines 68-73 of a file whose hunk starts at 70, and the two lines outside it cost the whole
comment — it was reported as a failed post and never published. A model reads the code around a
change and cites what it read; the diff is narrower than that by construction, so this is ordinary
rather than exceptional. Unanchored is worse than anchored and far better than a critical finding
nobody sees. The retry is matched on the status alone, since `422` also covers a malformed comment;
if the unanchored post fails too, the item reports that second failure, which is the honest one.

**Inputs / outputs**: `host, owner, repo, pr_number, content, file_path, start_line, end_line,
commit_id, token` → `long` (comment id).
**Edge cases**: a single-line comment (`start_line == end_line`) omits `start_line`/`start_side`
entirely — sending them with `start_line == line` 422s per the source comment.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01).

### PROV-012 GitHub general (issue) comment
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `POST .../issues/{pr_number}/comments` with `{ body: content }`. Used for the
summary comment and any finding whose location couldn't be parsed.
**Inputs / outputs**: `host, owner, repo, pr_number, content, token` → `long`.
**Edge cases**: none beyond generic error mapping; the returned id is not threaded (issue comments
have no reply relationship).
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01).

### PROV-013 GitHub reply to review comment
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `POST .../pulls/{pr_number}/comments/{comment_id}/replies` with `{ body: content }`.
**Inputs / outputs**: `host, owner, repo, pr_number, comment_id, content, token` →
`void`.
**Edge cases**: none beyond generic error mapping.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01).

### PROV-014 GitHub review-thread resolution (GraphQL)
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: two sequential `POST {graphql_root}` calls — see "The GraphQL call" above for the
`VERBATIM` query and mutation text. Finds the review thread containing `comment_id`'s
`databaseId` among the first 100 threads × first 100 comments each, then calls
`resolveReviewThread`.
**Inputs / outputs**: `host, owner, repo, pr_number, comment_id, token` → `void`.
**Edge cases**: no REST equivalent exists, hence GraphQL. A PR with more than 100 review threads,
or a thread with more than 100 comments, may not be found even if the comment exists. GraphQL
error strings omit the response body (`"GitHub GraphQL returned {status}"` /
`"GitHub GraphQL resolve returned {status}"`), unlike every REST error string in this file.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope), which
per the source comment treats a failure here as best-effort (never fails the surrounding post).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01) — the one GraphQL path, executed and confirmed (`isResolved: true`).

### PROV-015 GitHub viewer decision
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: resolves the viewer's login via `get_authenticated_user`, then `GET
.../pulls/{number}/reviews?per_page=100`, filters to that login's reviews, and folds them in
response order: `APPROVED`→`"approved"`, `CHANGES_REQUESTED`→`"changes_requested"`,
`DISMISSED`→`"none"`, anything else leaves the running value unchanged. The **last** matching
review wins.
**Inputs / outputs**: `host, owner, repo, number, token` → `string` (one of
`"approved" | "changes_requested" | "none"`).
**Edge cases**: capped at 100 reviews with no further pagination. Relies on GitHub returning
reviews in submission order (not independently verified in the source — assumed, not asserted).
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-016 GitHub submit review
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `POST .../pulls/{pr_number}/reviews` with `{ event }`
(`"APPROVE" | "REQUEST_CHANGES"`); `body: content` added only when `content.trim()` is non-empty.
**Inputs / outputs**: `host, owner, repo, pr_number, event, body, token` → `void`.
**Edge cases**: GitHub itself requires a non-empty body for `REQUEST_CHANGES` — this function does
not pre-validate that; an empty body on a `REQUEST_CHANGES` event would surface as a generic
non-2xx error from GitHub, not a distinct local error.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01 — the live call returned the 422 self-approval,
classified per `XLANG-013`; a 2xx APPROVE needs a second account, see the endpoint note).

### PROV-017 GitHub close pull request
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: `PATCH .../pulls/{pr_number}` with `{ state: "closed" }`.
**Inputs / outputs**: `host, owner, repo, pr_number, token` → `void`.
**Edge cases**: closes without merging; no confirmation of the prior state.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-018 GitHub comment-thread listing and grouping
**Implementation**: `src/CodeFlow.App/Providers/GitHub/GitHubClient.cs`
**Behaviour**: fetches `GET .../pulls/{pr_number}/comments?per_page=100` (inline review comments)
and `GET .../issues/{pr_number}/comments?per_page=100` (conversation comments). Inline comments
are grouped into threads keyed by `root_id = in_reply_to_id.unwrap_or(id)`: the first comment seen
for a given `root_id` creates the thread (`file_path = path`, `start_line = start_line.or(line)`,
`end_line = line`); subsequent comments with the same `root_id` are appended. Threads are emitted
in the order their root id was **first seen** in the response, not sorted by id or date. Empty
(whitespace-only or absent) `body` comments are dropped before grouping. Issue comments are then
appended as their own PR-level threads (`file_path/start_line/end_line = None`), one thread per
comment (issue comments aren't threaded by GitHub).
**Inputs / outputs**: `host, owner, repo, pr_number, token` → `IReadOnlyList, string>`.
**Edge cases**: **`AMBIGUOUS-PROV-a`** — the grouping algorithm assumes each reply comment is
returned *after* its root comment in the API response (so the root's `HashMap` entry already
exists when the reply is processed). If GitHub ever returned a reply before its root — the source
does not verify or enforce any ordering, and no explicit `sort` parameter is sent on this
endpoint — the reply would instead create the thread entry itself (using its own content as the
thread's first/only comment at that point), and the root comment arriving later would be appended
*after* it, silently reordering the thread's comments. Not resolved by guessing; needs either a
confirmed ordering guarantee from GitHub's docs or an explicit sort before porting.
**Frontend dependency**: not established in this document's scope.
**Markers**: `AMBIGUOUS-PROV-a`.

### PROV-019 Azure Basic-auth PAT header
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`, applied at every call site in `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `Authorization: Basic {base64(":" + pat)}` — empty username, PAT as password.
**Inputs / outputs**: `pat: string` → header value `string`.
**Edge cases**: no validation of PAT shape before sending.
**Frontend dependency**: not established in this document's scope (no `CredentialStore` call for the
ADO PAT appears anywhere in this document's files).
**Markers**: none.

### PROV-020 Azure organization normalization
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `normalize_org` reduces whatever form the org was saved in — bare name, full
`https://dev.azure.com/{org}` URL, legacy `https://{org}.visualstudio.com` URL — to the bare org
name, so it can be percent-encoded and interpolated into a path segment safely (a raw `:` in the
path fails IIS request validation on Azure's server).
**Inputs / outputs**: `org: string` → normalized `string`.
**Edge cases**: an org string that matches none of the three recognized prefixes is returned
trimmed but otherwise unchanged (assumed to already be a bare name).
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-021 Azure path-segment percent-encoding
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `encode_segment` percent-encodes every byte outside `A-Za-z0-9-._~` as `%{byte:02X}`.
Applied to `org` (post-`normalize_org`) and `project` at every call site; **not** applied
consistently to `repo_id` — see `BUG-PROV-a` above.
**Inputs / outputs**: `s: string` → percent-encoded `string`.
**Edge cases**: none beyond the inconsistency noted in `BUG-PROV-a`.
**Frontend dependency**: not established in this document's scope.
**Markers**: `BUG-PROV-a` (see the full call-site enumeration in the "Path-segment
percent-encoding" section above).

### PROV-022 Azure git-remote detection
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `detect_from_remote_url` recognizes three shapes: SSH
(`git@ssh.dev.azure.com:v3/{org}/{project}/{repo}`), `dev.azure.com` HTTPS
(`https://dev.azure.com/{org}/{project}/_git/{repo}`), and legacy `{org}.visualstudio.com` HTTPS
(with an optional `/DefaultCollection` prefix). Path segments are decoded with
`decode_path_segment` (`src/CodeFlow.App/Providers/Azure/AzureClient.cs`).
**Inputs / outputs**: `remote_url: string` → `DetectedAdoRepo{org,project,repo}?`.
**Edge cases**: **`BUG-PROV-b`** — `decode_path_segment` is `s.replace("%20", " ")`: it only
unescapes a literal space (`%20`) and leaves every other percent-escape untouched (e.g. an
accented character like `%C3%A9` for "é" in a project name stays percent-encoded), unlike
`src/CodeFlow.App/Providers/PrLink.cs`'s `percent_decode`, which is a full percent-decoder. Suspected-correct behavior: use
a general percent-decoder here too, consistent with `src/CodeFlow.App/Providers/PrLink.cs`. Ported as-is.
**Frontend dependency**: not established in this document's scope.
**Markers**: `BUG-PROV-b`.

### PROV-023 Azure list projects / list repos
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `list_projects`: `GET {org}/_apis/projects?api-version=7.1`. `list_repos`: `GET
{org}/{project}/_apis/git/repositories?api-version=7.1`. Both return `{ value: [...] }` unwrapped
into a plain `Vec`.
**Inputs / outputs**: `org, (project,) pat` → `IReadOnlyList|IReadOnlyList<AdoRepo>, string>`.
**Edge cases**: no explicit pagination handling — this document cannot establish from the source
whether the server enforces a default page size on either endpoint.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-024 Azure status bucketing and PR mapping
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `bucket_status(status, is_draft)`: `"completed"`→`"merged"`, `"abandoned"`→
`"closed"`, else `is_draft`→`"draft"`, else `"open"`. `map_pull_request` also strips the
`refs/heads/` prefix from both branch refs (`strip_ref`) and synthesizes `url` as
`https://dev.azure.com/{org_enc}/{project_enc}/_git/{repo_name_enc}/pullrequest/{id}` — read from
nowhere in the API response.
**Inputs / outputs**: `status: string, is_draft: bool` → bucket string; `(org_enc, project_enc,
RawPullRequest)` → `PullRequestSummary`.
**Edge cases**: none beyond the bucket precedence itself.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-025 Azure list/get pull request
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `list_pull_requests`: `GET .../pullrequests?searchCriteria.status=all&api-version=7.1`,
no explicit page size set. `get_pull_request`: `GET .../pullRequests/{pr_id}?api-version=7.1`;
`project`/`repo_id` may each be a GUID or a name; additionally recovers the project's **name**
from `repository.project.name` on the response (falling back to the caller-supplied `project` if
absent), returned alongside the summary as `AdoPullRequest{summary, project_name, repo_name}`.
**Inputs / outputs**: `org, project, repo_id, (pr_id,) pat` →
`IReadOnlyList|AdoPullRequest, string>`.
**Edge cases**: **`AMBIGUOUS-PROV-c`** — `list_pull_requests` sets no `$top`/page-size query
parameter at all; this document cannot establish from the source what page size, if any, Azure's
server defaults to for this endpoint, unlike GitHub's explicit `per_page=100`.
**Frontend dependency**: not established in this document's scope.
**Markers**: `AMBIGUOUS-PROV-c`.

### PROV-026 Azure create pull request
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `POST .../pullrequests?api-version=7.1` with `{ sourceRefName: "refs/heads/{source_branch}",
targetRefName: "refs/heads/{target_branch}", title, description, isDraft: draft }`.
**Inputs / outputs**: `org, project, repo_id, title, description, source_branch, target_branch,
draft, pat` → `PullRequestSummary`.
**Edge cases**: branch names are given bare and the function always adds the `refs/heads/` prefix
— passing an already-prefixed branch name would double it (not guarded against in the source).
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-027 Azure latest-iteration lookup
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `GET .../pullRequests/{pr_id}/iterations?api-version=7.1`, takes the last item's
`id`, falling back to `1` if the list is empty.
**Inputs / outputs**: `org, project, repo_id, pr_id, pat` → `long`.
**Edge cases**: the fallback to `1` is a deliberate best-effort choice per the source comment, not
an error condition.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-028 Azure diff assembly
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `pull_request_diff` fetches `GET .../pullRequests/{pr_id}/iterations/{iteration_id}/changes?$top=1000&api-version=7.1`
(against the base, no `$compareTo`). For each non-folder change entry: path is
`item.path.trim_start_matches('/')` (Azure paths are absolute within the repo); `old_id` is
`None` when `change_type` contains `"add"`, else `item.originalObjectId` filtered to exclude the
empty string and the all-zero `NULL_OBJECT_ID` (`"000...0"`, 40 zeros); `new_id` is `None` when
`change_type` contains `"delete"`, else `item.objectId` filtered the same way. Files past
`MAX_DIFF_FILES = 80` are dropped (with a trailing note appended: `"(only the first {80} of
{total} changed files are included)\n"`). For each kept file, its two blobs are fetched **in
sequence** — `src/CodeFlow.App/Providers/Azure/AzureClient.cs` awaits the base side and then the target side; only *distinct files*
overlap (stream.iter(...).buffered(6)` — at most 6 in-flight file renders at once,
each render itself issuing up to two `get_blob` calls, and `buffered` preserves output order unlike
`buffer_unordered`); a blob over `MAX_BLOB_BYTES = 512
KiB` on either side renders as `"diff --git a/{path} b/{path}\n({change}, too large to display)\n"`
instead of being fetched-and-diffed further (the blob **is** fetched — the size check happens
after both fetches complete, not before). A blob fetch failure on either side renders as
`"diff --git a/{path} b/{path}\n(couldn't read this file from Azure DevOps)\n"`. Otherwise renders
via `unified_patch` (PROV-030); if `git2::a blob-to-blob patch fails (binary content, e.g.),
renders as `"diff --git a/{path} b/{path}\n({change}, binary)\n"`.
**Inputs / outputs**: `org, project, repo_id, pr_id, pat` → `string`.
**Edge cases**: an empty result (no file changes) errors `"This pull request has no file changes
to review"`. Truncation past 80 files is silent in the diff body itself, only noted in the trailing
line.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-029 Azure unified-diff rendering
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `unified_patch(path, old, new)` calls `git2::a blob-to-blob patch(old, Some(path),
new, Some(path), None)` then `.to_buf()`, returning the rendered patch text. The same path is used
for both sides — this function never renders a rename.
**Inputs / outputs**: `path: string, old: byte[], new: byte[]` → `string?`.
**Edge cases**: returns `None` (not an error) if libgit2 fails to produce a patch, e.g. for binary
content — the caller treats that as "binary" rather than propagating an error.
**Frontend dependency**: none directly — exercised by `pull_request_diff` (PROV-028).
**Markers**: none.
**Test coverage**: `unified_patch_renders_a_git_style_diff`, `unified_patch_handles_added_and_deleted_files`
— see `test-vectors/ado.vectors.json`.

### PROV-030 Azure anchored comment thread
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `POST .../pullRequests/{pr_id}/threads?api-version=7.1` with the body shown in full
under "Comment-thread positioning" above — `filePath` gets a leading slash added if missing (never
stripped), `rightFileStart`/`rightFileEnd` carry `{ line, offset: 1 }`,
`pullRequestThreadContext.iterationContext` pins the comparison to `{1, latest_iteration}`. Calls
`get_latest_iteration_id` first. Returns the created thread's id.
**Inputs / outputs**: `org, project, repo_id, pr_id, content, file_path, start_line, end_line, pat`
→ `long` (thread id).
**Edge cases**: `end_line.max(start_line)` guards against an inverted range.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01); shares `BUG-PROV-a`'s unencoded-`repo_id` defect (both its own
thread-creation URL and its internal `get_latest_iteration_id` call interpolate `repo_id` raw).

### PROV-031 Azure general comment thread
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`, shared POST via `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `POST .../pullRequests/{pr_id}/threads?api-version=7.1` with
`{ comments: [{ parentCommentId: 0, content, commentType: 1 }], status: 1 }` — no `threadContext`,
so this is a PR-level (non-anchored) thread. `post_thread` is the shared POST-and-parse-id helper
used by both this and PROV-030.
**Inputs / outputs**: `org, project, repo_id, pr_id, content, pat` → `long` (thread id).
**Edge cases**: none beyond generic error mapping.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01); `BUG-PROV-a` (unencoded `repo_id`).

### PROV-032 Azure thread reply
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `POST .../threads/{thread_id}/comments?api-version=7.1` with
`{ parentCommentId: 1, content, commentType: 1 }` — `parentCommentId` hardcoded to `1`, relying on
the root comment of any thread this app created always being comment id 1 within that thread.
**Inputs / outputs**: `org, project, repo_id, pr_id, thread_id, content, pat` → `void`.
**Edge cases**: relies on the assumption above; not re-derived from the thread's actual comment
list at call time.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` (§2.9, 2026-08-01); `BUG-PROV-a` (unencoded `repo_id`).

### PROV-033 Azure thread status
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `PATCH .../threads/{thread_id}?api-version=7.1` with `{ status }`. Status ints,
`VERBATIM`: `1`=active, `2`=fixed, `3`=wontFix, `4`=closed, `5`=byDesign, `6`=pending.
**Inputs / outputs**: `org, project, repo_id, pr_id, thread_id, status: int, pat` →
`void`.
**Edge cases**: no validation that `status` is one of the six known values before sending.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `UNVERIFIED` (§2.9 — unreachable in the 2026-08-01 live run, see the endpoint note);
`BUG-PROV-a` (unencoded `repo_id`).

### PROV-034 Azure authenticated-user id and reviewer vote
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `authenticated_user_id`: `GET {org}/_apis/connectionData?api-version=7.1-preview`
(org-scoped, the one endpoint using `PREVIEW_API_VERSION`). `set_reviewer_vote`: fetches that id,
then `PUT .../pullRequests/{pr_id}/reviewers/{user_id}?api-version=7.1` with `{ vote }` — adds the
caller as a reviewer if not already one, and sets the vote, in one call.
**Inputs / outputs**: `org, (project, repo_id, pr_id, vote: int,) pat` →
`string|()`.
**Edge cases**: none beyond generic error mapping.
**Frontend dependency**: indirect, via `src/CodeFlow.App/Review/ReviewCommands.cs` (out of this document's scope).
**Markers**: `VERIFIED-LIVE` on `set_reviewer_vote` (§2.9, 2026-08-01); `BUG-PROV-a` (unencoded `repo_id`, on
`set_reviewer_vote`'s URL — `authenticated_user_id`'s own URL has no `repo_id` to encode).

### PROV-035 Azure viewer decision
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: fetches the authenticated user's id, then `GET .../pullRequests/{pr_id}?api-version=7.1`
(same shape as `get_pull_request`, which includes `reviewers`), finds the entry whose `id` matches
(case-insensitive), reads `vote`: `> 0`→`"approved"`, `< 0`→`"changes_requested"`, `0`/absent→`"none"`.
**Inputs / outputs**: `org, project, repo_id, pr_id, pat` → `string`.
**Edge cases**: collapses vote `5` into the same `"approved"` bucket as `10`, and `-5` into the
same `"changes_requested"` bucket as `-10` — see "Shared concepts" for why this isn't unified with
GitHub's model.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-036 Azure abandon pull request
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `PATCH .../pullRequests/{pr_id}?api-version=7.1` with `{ status: "abandoned" }`.
**Inputs / outputs**: `org, project, repo_id, pr_id, pat` → `void`.
**Edge cases**: none beyond generic error mapping.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-037 Azure comment-thread listing and filtering
**Implementation**: `src/CodeFlow.App/Providers/Azure/AzureClient.cs`
**Behaviour**: `GET .../pullRequests/{pr_id}/threads?api-version=7.1`. Filters to threads whose
`status`, lowercased, is `"active"`, `"pending"`, or absent (`None`) — `"fixed"`, `"wontfix"`,
`"closed"`, `"bydesign"` threads are dropped. Within a kept thread, comments are filtered to
`commentType` `"text"` (default when absent) with non-empty trimmed `content` — system-generated
comments (vote changes, iteration notices) carry other `commentType`s and are dropped. A thread
left with zero comments after that filtering is dropped entirely. `threadContext.filePath` /
`rightFileStart.line` / `rightFileEnd.line` are carried through as-is (no leading-slash
normalization on read, unlike the write path).
**Inputs / outputs**: `org, project, repo_id, pr_id, pat` → `IReadOnlyList, string>`.
**Edge cases**: a thread with `status: null` in the response is treated as open (kept) — same
bucket as `"active"`/`"pending"`.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-038 pr_link percent-decoding
**Implementation**: `src/CodeFlow.App/Providers/PrLink.cs`
**Behaviour**: hand-rolled `%XX` decoder; a `%` not followed by two valid hex digits is emitted
literally rather than dropped or erroring; output run through lossy UTF-8 decoding.
**Inputs / outputs**: `s: string` → decoded `string`.
**Edge cases**: a truncated escape at the very end of the string, or one followed by non-hex
characters, is left as literal text.
**Frontend dependency**: none directly (pure helper of `split`, PROV-039).
**Markers**: none.

### PROV-039 pr_link URL splitting
**Implementation**: `src/CodeFlow.App/Providers/PrLink.cs`
**Behaviour**: strips fragment (from first `#`), query (from first `?`), trailing `/`; strips an
optional `https://`/`http://` scheme; strips userinfo (`user@`, via `rsplit('@')`); splits host
from path on the first `/`; percent-decodes each non-empty path segment.
**Inputs / outputs**: `url: string` → `(string Host, IReadOnlyList<string> Segments)?`.
**Edge cases**: `None` if there's no `/` after the host, or the host half is empty (e.g. `"https:///foo"`).
**Frontend dependency**: none directly.
**Markers**: none.

### PROV-040 GitHub PR link grammar
**Implementation**: `src/CodeFlow.App/Providers/PrLink.cs`
**Behaviour**: `host` must match an entry in `known_github_hosts` (case-insensitive); segments
must be `[owner, repo, kind, number, ..]` with `kind` case-insensitively `"pull"`/`"pulls"`; `repo`
has a trailing `.git` stripped; `number` parses as `long`. Returned `host` is the matched
allowlist entry.
**Inputs / outputs**: `host: string, segments: IReadOnlyList<string>, known_github_hosts: IReadOnlyList<string>` →
`PrLinkTarget.GitHub?`.
**Edge cases**: trailing segments after `number` (`/files`, `#fragment` already stripped upstream)
are ignored via `..`.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.
**Test coverage**: `parses_github_links`, `rejects_non_pr_links` — see `test-vectors/pr_link.vectors.json`.

### PROV-041 Azure PR link grammar
**Implementation**: `src/CodeFlow.App/Providers/PrLink.cs`
**Behaviour**: `dev.azure.com` host: `org` = first segment, rest matched against
`[project, "_git", repo, "pullrequest"|"pullrequests", number, ..]` or (project omitted)
`[.._git", repo, "pullrequest"|"pullrequests", number, ..]` with `project` defaulting to `repo`.
`*.visualstudio.com` host: `org` = the subdomain, same two-shape match against the full segment
list after optionally stripping a leading `"DefaultCollection"` segment (case-insensitive).
`"_git"` and `"pullrequest"/"pullrequests"` matched case-insensitively.
**Inputs / outputs**: `host: string, segments: IReadOnlyList<string>` → `PrLinkTarget.Azure?`.
**Edge cases**: a link with no `_git`/`pullrequest` segments in the recognized position returns
`None` (rejects a repo-browse link).
**Frontend dependency**: not established in this document's scope.
**Markers**: none.
**Test coverage**: `parses_azure_links`, `rejects_non_pr_links` — see `test-vectors/pr_link.vectors.json`.

### PROV-042 pr_link dispatch
**Implementation**: `src/CodeFlow.App/Providers/PrLink.cs`
**Behaviour**: `parse(url, known_github_hosts)` calls `split`, then tries `parse_github`, falling
back to `parse_azure`; first `Some` wins.
**Inputs / outputs**: `url: string, known_github_hosts: IReadOnlyList<string>` → `PrLinkTarget?`.
**Edge cases**: a URL matching neither grammar, or that doesn't even split into host+path, returns
`None` — the caller (ado_cmd.resolve_pr_link, out of scope) is responsible for
telling the user "not a recognized PR link" vs. any more specific reason.
**Frontend dependency**: not established in this document's scope.
**Markers**: none.

### PROV-043 GitHub token loaded from storage (command boundary)
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: `github_authenticated_user(host)` loads the token via
`CredentialStore.Get`(&`CredentialStore.GitHubTokenKey`(&host))`, erroring `"No GitHub token saved for
this host"` if absent, then calls `get_authenticated_user` (PROV-005).
**Inputs / outputs**: `host: string` → `string` (the login).
**Edge cases**: the token-key format itself (`CredentialStore.GitHubTokenKey`) is owned by the
storage/secrets document, not this one.
**Frontend dependency**: `githubAuthenticatedUser` — see `01-ipc-surface.md`.
**Markers**: none.

### PROV-044 Manual GitHub project linking (command boundary)
**Implementation**: `src/CodeFlow.App/Providers/ProviderCommands.cs`
**Behaviour**: `link_project_github(id, github_owner, github_repo, github_host)` writes directly to
the `projects` table via `src/CodeFlow.App/Storage/` store — no network call. Fallback path for
when git-remote auto-detection (PROV-003) didn't resolve a repo.
**Inputs / outputs**: `id, github_owner, github_repo, github_host: string` → `void`.
**Edge cases**: no validation that `github_owner`/`github_repo`/`github_host` actually correspond
to a reachable repo — this command only records the association.
**Frontend dependency**: `linkProjectGithub` — see `01-ipc-surface.md`.
**Markers**: none.

## Test coverage

| extracted case | Source | Fixture | Kind |
|---|---|---|---|
| `unified_patch_renders_a_git_style_diff` | `src/CodeFlow.App/Providers/Azure/AzureClient.cs` | `ado.vectors.json#unified-patch-modify-single-line` | vector |
| `unified_patch_handles_added_and_deleted_files` | `src/CodeFlow.App/Providers/Azure/AzureClient.cs` | `ado.vectors.json#unified-patch-added-file`, `ado.vectors.json#unified-patch-deleted-file` | vector |
| `parses_github_links` | `src/CodeFlow.App/Providers/PrLink.cs` | `pr_link.vectors.json#github-basic`, `#github-deep-link-with-tab-query-fragment`, `#github-enterprise-connected-host`, `#github-enterprise-unknown-host-rejected` | vector |
| `parses_azure_links` | `src/CodeFlow.App/Providers/PrLink.cs` | `pr_link.vectors.json#azure-full-org-project-repo-with-encoded-space`, `#azure-legacy-visualstudio-host-with-defaultcollection-and-query`, `#azure-project-segment-omitted-defaults-to-repo-name` | vector |
| `rejects_non_pr_links` | `src/CodeFlow.App/Providers/PrLink.cs` | `pr_link.vectors.json#reject-repo-root-not-a-pr`, `#reject-github-issue-link`, `#reject-azure-repo-link-no-pr-segment`, `#reject-not-a-url` | vector |

5 extracted case functions in this document's scope (`src/CodeFlow.App/Providers/GitHub/GitHubClient.cs` and `src/CodeFlow.App/Providers/ProviderCommands.cs` carry
none), all extracted as data — no `behavioural`-only entries were needed.

## Markers raised

| Local id | Kind | Summary |
|---|---|---|
| `BUG-PROV-a` | Bug | `src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s `encode_segment` is applied to `repo_id` in `get_pull_request`, `viewer_decision`, and `pull_request_diff`'s blob/changes URLs, but **not** in `list_pull_requests`, `create_pull_request`, `get_latest_iteration_id`, `post_pr_comment_anchored`, `post_pr_comment`, `reply_pr_thread`, `set_pr_thread_status`, `set_reviewer_vote`, or `abandon_pull_request`, all of which interpolate `repo_id` raw. A repository referenced by a name containing a space or other reserved character is affected inconsistently depending on which function is called. |
| `BUG-PROV-b` | Bug | `src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s `decode_path_segment` (used by `detect_from_remote_url`) only unescapes `%20`; every other percent-escape in an org/project/repo name parsed from a git remote is left encoded, unlike `src/CodeFlow.App/Providers/PrLink.cs`'s full `percent_decode`. |
| `AMBIGUOUS-PROV-a` | Ambiguity | GitHub's `list_pr_comment_threads` groups inline review comments into threads assuming the API returns each reply after its root comment; this ordering is not verified or enforced in the source. **Observed to hold** in the 2026-08-01 live run (six threads, up to three comments each, listed correctly through the app after every publish) — an observation, not a guarantee, so the ambiguity stays open. |
| `AMBIGUOUS-PROV-b` | Ambiguity | No status-code-specific branch exists anywhere in `src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s error mapping; how ADO PAT expiry is detected and surfaced as a distinct UI state (rather than a generic network error) is not decided in these files. |
| `AMBIGUOUS-PROV-c` | Ambiguity | `src/CodeFlow.App/Providers/Azure/AzureClient.cs`'s `list_pull_requests` sets no `$top`/page-size parameter; the server's effective default page size for this endpoint cannot be established from the source. The 2026-08-01 live run could not settle it either — the throwaway repo held a single PR, far below any plausible default cap. |
| `DIVERGENCE-PROV-a` | Divergence | GitHub review events and Azure DevOps numeric reviewer votes are deliberately not unified into one shared type — preserve both models separately. |
| `UNVERIFIED` | — | Now applies to **one** function: Azure's `set_pr_thread_status`. Everything else in the old list (GitHub: `post_pr_comment_anchored`, `post_pr_comment`, `reply_pr_review_comment`, `resolve_review_thread_for_comment`, `submit_pr_review`; Azure: `post_pr_comment_anchored`, `post_pr_comment`, `reply_pr_thread`, `set_reviewer_vote`) is `VERIFIED-LIVE` since the 2026-08-01 throwaway-PR run recorded in `90-ambiguities.md` — executed from the app against real GitHub and Azure DevOps APIs and cross-checked from outside the app. `set_pr_thread_status` stayed out of reach because it only fires when a re-review resolves a posted finding, which needs a second push the throwaway Azure repo did not allow. |
| `VERIFIED-LIVE` | — | Executed against the real host API in the 2026-08-01 live run (see `90-ambiguities.md` for the full record), with the result cross-checked through the host's own API from outside the app. A verification is one observation, not a contract: it pins what the host accepted that day. |
