#!/usr/bin/env bash
#
# Builds the macOS installer and its digest, locally.
#
#   scripts/build-dmg.sh
#
# The .dmg has never come from Actions and is not going to: `hdiutil` has no substitute, and on a
# private repository macOS runner minutes bill at ten times Linux. This wraps the three steps that
# producing an installable .dmg actually takes — build, locate, hash — so getting one is a single
# command that touches no git state and talks to no remote. `publish-release.sh` calls it for the
# same three steps rather than repeating them.
#
# The `.sha256` beside it is not optional. `UpdateService.ExpectedDigestAsync` looks for an asset
# named `<installer>.sha256` and refuses the update before downloading anything when it is missing,
# so a .dmg published without one cannot be installed from inside the app.
#
# Progress goes to stderr and the artefact path to stdout, so a caller can read the path directly:
#
#   dmg="$(scripts/build-dmg.sh)"

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

say() { echo "$@" >&2; }

if [[ "$(uname -s)" != "Darwin" ]]; then
  say "the .dmg can only be built on macOS — hdiutil has no substitute"
  exit 65
fi

version="$(node -p "require('./shell/package.json').version")"

say "==> building the macOS installer (v${version})"
scripts/build-app.sh mac >&2

# Named from the declared version rather than picked off a listing. `dist-installers/` is not
# cleaned between builds, so a previous version's .dmg sits right next to this one — and being
# alphabetically first, that is what `ls | head -1` would hand back.
#
# The name is electron-builder's default pattern, `${productName}-${version}-${arch}.${ext}`;
# `shell/electron-builder.yml` sets no `artifactName`, so changing that there means changing it here.
dmg="dist-installers/CodeFlow-${version}-arm64.dmg"
if [[ ! -f "$dmg" ]]; then
  say "expected $dmg after the build, but it is not there"
  exit 70
fi

# `shasum` records whatever path it was given, so it runs from the artefact's own directory to keep
# the recorded name bare — the updater matches the asset by name, and a recorded `dist-installers/…`
# would not match what is uploaded.
say "==> hashing $(basename "$dmg")"
(cd "$(dirname "$dmg")" && shasum -a 256 "$(basename "$dmg")" > "$(basename "$dmg").sha256")

say
say "Built:"
say "  $dmg"
say "  ${dmg}.sha256"
echo "$dmg"
