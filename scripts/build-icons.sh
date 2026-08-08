#!/usr/bin/env bash
#
# Regenerates the packaged icons from their SVG masters.
#
#   scripts/build-icons.sh
#
# The rasterised files are committed, because electron-builder reads them at package time and
# `build-app.sh` has no step that could produce them. This exists so the masters
# (`shell/assets/icon.svg`, `shell/assets/tray.svg`) stay the source of truth rather than becoming
# decoration next to binaries nobody can rebuild.
#
# macOS only, and deliberately dependency-free: `qlmanage`, `sips` and `iconutil` all ship with the
# system, so changing the mark costs nothing to install. The .ico container is packed by the Python
# helper beside this script — macOS writes .icns natively and .ico not at all.

set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "this script needs macOS: qlmanage, sips and iconutil have no cross-platform equivalent" >&2
  exit 64
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
assets="$root/shell/assets"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Quick Look is the only SVG rasteriser on a stock macOS; `sips` reads PNG and friends but not SVG.
render() {
  local source="$1" size="$2" out="$3"
  qlmanage -t -s "$size" -o "$work" "$source" >/dev/null 2>&1
  sips -s format png -z "$size" "$size" "$work/$(basename "$source").png" --out "$out" >/dev/null
}

echo "==> master 1024×1024"
render "$assets/icon.svg" 1024 "$assets/icon.png"

echo "==> icon.icns"
iconset="$work/icon.iconset"
mkdir -p "$iconset"
# The ten entries `iconutil` expects; a missing one is not an error, it is a size macOS then scales.
for spec in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" "64 icon_32x32@2x" \
            "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" "512 icon_256x256@2x" \
            "512 icon_512x512" "1024 icon_512x512@2x"; do
  sips -s format png -z "${spec%% *}" "${spec%% *}" "$assets/icon.png" \
    --out "$iconset/${spec##* }.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$assets/icon.icns"

echo "==> icon.ico"
ico_sizes=(16 24 32 48 64 128 256)
ico_files=()
for size in "${ico_sizes[@]}"; do
  sips -s format png -z "$size" "$size" "$assets/icon.png" --out "$work/ico-$size.png" >/dev/null
  ico_files+=("$work/ico-$size.png")
done
python3 "$here/pack-ico.py" "$assets/icon.ico" "${ico_files[@]}"

echo "==> tray.png (32×32, its own simplified cut of the mark)"
render "$assets/tray.svg" 512 "$work/tray-512.png"
sips -s format png -z 32 32 "$work/tray-512.png" --out "$assets/tray.png" >/dev/null

echo
echo "done — commit the regenerated icon.png, icon.icns, icon.ico and tray.png"
