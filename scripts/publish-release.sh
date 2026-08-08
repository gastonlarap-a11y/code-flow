#!/usr/bin/env bash
#
# Cuts a release the in-app updater can find.
#
#   scripts/publish-release.sh v1.7.3
#
# The two installers come from two machines and neither can produce the other's, so a release is
# assembled rather than built in one place. Pushing the tag starts the Windows job in
# `.github/workflows/release.yml`, which attaches its `.exe` on its own; the macOS `.dmg` is built
# here and uploaded from here, because `hdiutil` has no substitute and this repository is private,
# where macOS runner minutes bill at ten times Linux.
#
# Nothing is signed. The release is private, and `update_check` reads it with a credential the user
# already has — the GitHub token in the OS keychain, or `gh auth token`. No token is ever committed.

set -euo pipefail

tag="${1:-}"

if [[ ! "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+ ]]; then
  echo "usage: scripts/publish-release.sh vX.Y.Z" >&2
  exit 64
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="${tag#v}"
declared="$(node -p "require('./shell/package.json').version")"

# The app compares this tag against what the shell reports through `app.getVersion()`, which reads
# `shell/package.json`. A tag that disagrees with it produces an update the installed build then
# reports as still available, forever.
if [[ "$version" != "$declared" ]]; then
  echo "tag $tag does not match shell/package.json ($declared) — bump one of them first" >&2
  exit 65
fi

if [[ -n "$(git status --porcelain)" ]]; then
  echo "the working tree is dirty; commit or stash before cutting a release" >&2
  exit 65
fi

# Build, locate and hash: all three live in `build-dmg.sh`, which exists so the same .dmg can be
# produced without cutting a release at all. It prints the artefact path on stdout and everything
# else on stderr. The digest is written there too, before the tag is pushed — a release whose .dmg
# carries no `.sha256` is one the in-app updater refuses.
dmg="$(scripts/build-dmg.sh)"

echo "==> tagging $tag"
git tag "$tag"
git push origin "$tag"

# Both sides create the release — this script and the Windows job's `action-gh-release` — because
# either may finish first and the upload below needs somewhere to go. So this is idempotent rather
# than a race: whoever gets there second finds it already made and moves on.
# `--notes-from-tag` is not used: the tag is lightweight and carries none.
if gh release view "$tag" >/dev/null 2>&1; then
  echo "==> release $tag already exists (the Windows job got there first)"
else
  echo "==> creating the release"
  gh release create "$tag" --title "$tag" --generate-notes
fi

echo "==> uploading $(basename "$dmg")"
gh release upload "$tag" "$dmg" "${dmg}.sha256" --clobber

echo
echo "Done. The Windows installer is attached by the release workflow when its job finishes:"
echo "  gh run watch \$(gh run list --workflow=release --limit=1 --json databaseId --jq '.[0].databaseId')"
