#!/usr/bin/env bash
#
# Builds an installable CodeFlow for one platform.
#
#   scripts/build-app.sh mac       # .dmg and .zip, arm64 — needs macOS
#   scripts/build-app.sh win       # NSIS installer and portable .exe, x64
#   scripts/build-app.sh mac --dir # unpacked, no installer: the fast loop
#
# The app is three build products that have to arrive together, and nothing else in the repository
# builds more than one of them: `renderer/` and `shell/` are independent packages and the sidecar is
# a .NET solution. This is the only place that knows the whole order.
#
# A macOS installer can only be built on macOS — `hdiutil` has no substitute. A Windows installer
# can be built here, but NSIS needs Wine and electron-builder warns that large installers built that
# way have shipped broken; `.github/workflows/release.yml` builds it on a real Windows runner
# instead, which is also the only Windows this project has ever run on.

set -euo pipefail

target="${1:-}"
shift || true

case "$target" in
  mac) rid="osx-arm64" ;;
  win) rid="win-x64" ;;
  *)
    echo "usage: scripts/build-app.sh <mac|win> [electron-builder flags]" >&2
    exit 64
    ;;
esac

if [[ "$target" == "mac" && "$(uname -s)" != "Darwin" ]]; then
  echo "a macOS build needs macOS: the dmg target depends on hdiutil" >&2
  exit 64
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
staging="$root/shell/build"

cd "$root"

echo "==> renderer"
pnpm -C renderer install --frozen-lockfile
pnpm -C renderer build

echo "==> shell"
pnpm -C shell install --frozen-lockfile
pnpm -C shell build

echo "==> sidecar ($rid)"
# Self-contained so the machine needs no .NET runtime, and deliberately NOT single-file: a
# single-file bundle extracts its native libraries at runtime, and this carries three sets of them
# (SQLitePCLRaw, LibGit2Sharp, Porta.Pty). Those extracted copies are what code signing would
# later have to account for, so a single-file bundle is rejected here.
rm -rf "$staging"
mkdir -p "$staging"

dotnet publish src/CodeFlow.App/CodeFlow.App.csproj \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "$staging/core"

# Staged rather than referenced in place: electron-builder copies from a path it is given, and
# pointing it at `renderer/dist` would put the whole sibling package in the bundle.
cp -R "$root/renderer/dist" "$staging/renderer"

echo "==> installers"

# The previous installers for this platform go first, and only now — after the three build products
# above have all succeeded, so a failed build never leaves the machine with nothing installable.
#
# electron-builder names its output after the version in `shell/package.json`: rebuilding the same
# version overwrites, but every bump leaves the older one behind, and one macOS build is over 300 MB
# across its .dmg and its .zip. Only this platform's artefacts are removed. The other platform's are
# built somewhere else — `release.yml` builds Windows on a real runner — so a copy sitting here may
# be the only one on this machine.
output="$root/dist-installers"

case "$target" in
  mac) stale=("$output"/*.dmg "$output"/*-mac.zip "$output"/*-mac.zip.blockmap "$output/mac-arm64") ;;
  win) stale=("$output"/*.exe "$output"/*.exe.blockmap "$output/win-unpacked") ;;
esac

for artefact in "${stale[@]}"; do
  # A glob that matches nothing expands to itself, so existence is the test rather than the match.
  [[ -e "$artefact" ]] || continue
  echo "    removing $(basename "$artefact")"
  rm -rf "$artefact"
done

pnpm -C shell exec electron-builder --config electron-builder.yml "--$target" "$@"

echo
echo "Done. Installers are in dist-installers/."
