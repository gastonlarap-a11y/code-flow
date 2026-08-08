#!/usr/bin/env bash
#
# Cuts a release, end to end. This is the one command.
#
#   scripts/release.sh              # patch: 1.9.1 -> 1.9.2
#   scripts/release.sh minor        # 1.10.0
#   scripts/release.sh 2.0.0        # an explicit version
#   scripts/release.sh --fast       # skip the local suite; the CI gate still runs
#   scripts/release.sh --dry-run    # print the plan and stop before anything is written
#
# It bumps the version, refuses to proceed unless main is green and current, runs the suites here,
# pushes the bump, hands the build and upload to `publish-release.sh`, waits out the Windows job,
# and checks the finished release carries all four artefacts. Nothing is left for you to do.
#
# Not a `package.json` script, and that is not an accident: there is no package.json at the
# repository root — `AGENTS.md` says so and `release.yml` depends on it, which is why it has to
# point `pnpm/action-setup` at `shell/package.json`. `scripts/` is this repository's runner.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

bump="patch"
fast=""
dry=""

for arg in "$@"; do
  case "$arg" in
    --fast) fast=1 ;;
    --dry-run) dry=1 ;;
    major | minor | patch) bump="$arg" ;;
    [0-9]*.[0-9]*.[0-9]*) bump="$arg" ;;
    *)
      echo "usage: scripts/release.sh [major|minor|patch|X.Y.Z] [--fast] [--dry-run]" >&2
      exit 64
      ;;
  esac
done

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
fail() {
  printf '\n\033[31m%s\033[0m\n' "$1" >&2
  exit "${2:-65}"
}

# ---------------------------------------------------------------------------
# 1 · Guards, before anything is written
# ---------------------------------------------------------------------------

step "checking the ground"

# The .dmg half is built here because `hdiutil` has no substitute, so this script only works where
# that is possible. The Windows half is a runner's job either way.
[[ "$(uname -s)" == "Darwin" ]] || fail "a release needs macOS: the .dmg depends on hdiutil" 64

gh auth status >/dev/null 2>&1 || fail "gh is not authenticated — run 'gh auth login' first"

branch="$(git rev-parse --abbrev-ref HEAD)"
[[ "$branch" == "main" ]] || fail "releases are cut from main; you are on '$branch'"

[[ -z "$(git status --porcelain)" ]] || fail "the working tree is dirty; commit or stash first"

git fetch --quiet origin main
local_head="$(git rev-parse HEAD)"
remote_head="$(git rev-parse origin/main)"
if [[ "$local_head" != "$remote_head" ]]; then
  fail "main and origin/main disagree (local $local_head, remote $remote_head) — pull or push first"
fi

current="$(node -p "require('./shell/package.json').version")"

# `pnpm version` would compute this, but it also writes the file, and the tag has to be known before
# anything is touched: half the guards below are about the tag.
next_version() {
  local from="$1" how="$2" major minor patch
  IFS='.' read -r major minor patch <<<"${from%%-*}"
  case "$how" in
    major) echo "$((major + 1)).0.0" ;;
    minor) echo "${major}.$((minor + 1)).0" ;;
    patch) echo "${major}.${minor}.$((patch + 1))" ;;
    *) echo "$how" ;;
  esac
}

version="$(next_version "$current" "$bump")"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "'$version' is not a version number" 64
tag="v${version}"

# Both sides, because a tag deleted locally and still on the remote is the shape that produces a
# release nobody can reproduce.
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  fail "tag $tag already exists locally"
fi
if [[ -n "$(git ls-remote --tags origin "refs/tags/$tag")" ]]; then
  fail "tag $tag already exists on origin"
fi

echo "    ${current} -> ${version}   (tag ${tag})"

# ---------------------------------------------------------------------------
# 2 · main has to be green — for *this* commit
# ---------------------------------------------------------------------------

step "checking CI on main"

# Today this has an easy answer: no workflow verifies a commit any more — `ci-web` and `ci-sidecar`
# are parked in `.github/workflows-disabled/`, and `release.yml` only fires on a tag — so it normally
# finds nothing and the local suite below is the whole gate. It stays because it costs one API call
# and it is what makes re-enabling those workflows free: put them back and this picks them up with no
# edit here.
#
# Asked of the commit rather than of the workflows, because listing each workflow's newest run and
# demanding its head match main looks equivalent and is not: those workflows are path-filtered
# (`ci-sidecar` on `src/**`, `ci-web` on `renderer/**`, `shell/**`, `scripts/**`), so a shell-only
# commit never produces a sidecar run and that check would refuse a release forever, waiting for
# something that is never coming. A workflow that skipped is simply absent here, which is the correct
# answer rather than a missing one.
#
# What the head SHA buys is still the point: #60 was merged while `ci-web` was red, and a check that
# only looked at conclusions would have called that fine, because the run before it had passed.
checks="$(gh api "repos/{owner}/{repo}/commits/${remote_head}/check-runs" \
  --jq '.check_runs[] | "\(.name)\t\(.status)\t\(.conclusion)\t\(.html_url)"')"

if [[ -z "$checks" ]]; then
  # The normal case now that no workflow runs on a push. It means CI has verified nothing about this
  # tree, which is fine while the local suite still runs and not fine otherwise.
  [[ -z "$fast" ]] || fail "no CI ran for $remote_head and --fast skips the local suite: nothing would be verified"
  echo "    no CI ran for this commit — the local suite below is the only gate"
else
  while IFS=$'\t' read -r name run_status run_conclusion run_url; do
    [[ "$run_status" == "completed" ]] || fail "$name is still $run_status on main — wait for it: $run_url"
    case "$run_conclusion" in
      success | skipped | neutral) echo "    $name  $run_conclusion" ;;
      *) fail "$name is $run_conclusion on main: $run_url" ;;
    esac
  done <<<"$checks"
fi

if [[ -n "$dry" ]]; then
  cat <<EOF

Dry run. Nothing was written. What a real run would do next:

  1. $( [[ -n "$fast" ]] && echo "skip the local suite (--fast)" || echo "build and run every suite here" )
  2. bump shell/ and renderer/ to ${version}, commit 'chore(release): ${tag}', push to main
  3. scripts/publish-release.sh ${tag}   (.dmg, tag, GitHub release, upload)
  4. wait for the Windows job to attach its installer
  5. verify ${tag} carries the .dmg, the .exe and both .sha256 files
EOF
  exit 0
fi

# ---------------------------------------------------------------------------
# 3 · The suites, here
# ---------------------------------------------------------------------------

if [[ -n "$fast" ]]; then
  step "skipping the local suite (--fast)"
else
  step "building and testing locally"

  # Deliberately duplicates CI. This is the machine that produces the .dmg, and a release is the one
  # moment where "it passes here" has to be true rather than assumed — the bump commit itself never
  # goes through CI before it is tagged.
  dotnet build CodeFlow.slnx --configuration Release
  dotnet test CodeFlow.slnx --configuration Release --no-build

  pnpm -C shell install --frozen-lockfile
  pnpm -C shell test
  pnpm -C shell audit --audit-level moderate

  pnpm -C renderer install --frozen-lockfile
  pnpm -C renderer typecheck
  pnpm -C renderer test
  pnpm -C renderer audit --audit-level moderate
fi

# ---------------------------------------------------------------------------
# 4 · The bump
# ---------------------------------------------------------------------------

# Skipped when the version is already there, which is what makes a second run after a failure pick
# up where the first stopped instead of refusing. Without it, a build that dies at step 5 leaves
# main carrying a version with no release and no obvious way back.
if [[ "$current" == "$version" ]]; then
  step "shell/package.json is already at ${version} — resuming"
else
  step "bumping to ${version}"

  # `--no-git-tag-version` because the tag is `publish-release.sh`'s to push, and `--no-git-checks`
  # because the second call runs with the first one's edit already in the tree.
  pnpm -C shell version "$version" --no-git-tag-version --no-git-checks
  pnpm -C renderer version "$version" --no-git-tag-version --no-git-checks

  # Named individually: this commit is two files and must stay two files. `git add -A` here would
  # sweep up whatever else happened to be lying around into a release commit.
  git add shell/package.json renderer/package.json
  git commit -m "chore(release): ${tag}"
  git push origin main
fi

# ---------------------------------------------------------------------------
# 5 · Build, tag, publish — the script that already does this
# ---------------------------------------------------------------------------

step "publishing ${tag}"

# Unchanged and uncopied. Its own two guards — the tag matching shell/package.json, and a clean tree
# — are now guaranteed by the steps above rather than being something to trip over.
scripts/publish-release.sh "$tag"

# ---------------------------------------------------------------------------
# 6 · The Windows half
# ---------------------------------------------------------------------------

step "waiting for the Windows installer"

# On a tag push the run's headBranch *is* the tag. It does not appear the instant the tag lands, so
# this looks for it rather than assuming; a minute is far longer than GitHub has ever taken to
# register one.
run_id=""
for _ in $(seq 1 20); do
  run_id="$(gh run list --workflow release --limit 20 --json databaseId,headBranch \
    --jq "[.[] | select(.headBranch == \"$tag\")] | first | .databaseId // empty")"
  [[ -n "$run_id" ]] && break
  sleep 3
done

if [[ -z "$run_id" ]]; then
  fail "no release workflow run appeared for $tag. The macOS half is uploaded; start the other with:
  gh workflow run release -f tag=$tag" 70
fi

if ! gh run watch "$run_id" --exit-status; then
  fail "the Windows job failed. The macOS half is already attached to $tag; re-run just that half with:
  gh workflow run release -f tag=$tag" 70
fi

# ---------------------------------------------------------------------------
# 7 · Is the release actually complete?
# ---------------------------------------------------------------------------

step "checking the release"

# The updater refuses a release whose installer has no digest beside it (`Update/UpdateAssets.cs`),
# so a missing .sha256 is not cosmetic: it is a release that cannot be installed, and until now
# nothing looked. Matched by shape rather than by full name — the Windows arch is the runner's to
# decide, not this script's.
assets="$(gh release view "$tag" --json assets --jq '.assets[].name')"

missing=()
grep -qE "^CodeFlow-${version}-arm64\.dmg$" <<<"$assets" || missing+=("the macOS .dmg")
grep -qE "^CodeFlow-${version}-arm64\.dmg\.sha256$" <<<"$assets" || missing+=("the .dmg digest")
grep -qE "^CodeFlow-Setup-${version}-.*\.exe$" <<<"$assets" || missing+=("the Windows installer")
grep -qE "^CodeFlow-Setup-${version}-.*\.exe\.sha256$" <<<"$assets" || missing+=("the .exe digest")

if [[ ${#missing[@]} -gt 0 ]]; then
  printf '\n\033[31mrelease %s is incomplete — missing:\033[0m\n' "$tag" >&2
  printf '  - %s\n' "${missing[@]}" >&2
  printf '\nWhat it does carry:\n%s\n' "$assets" >&2
  exit 70
fi

printf '\n\033[32mDone.\033[0m %s is published with both installers and both digests.\n' "$tag"
gh release view "$tag" --json url --jq .url
