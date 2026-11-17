#!/usr/bin/env bash
# Renders apps/macos/Resources/AppIcon.icns from the SVG icon masters.
# Needs resvg (cargo install resvg). Re-run after scripts/gen-icon.mjs changes.
#
# macOS icons sit on an 824/1024 tile with transparent margin, so the masters
# are rendered with a widened viewBox instead of full-bleed.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
command -v resvg >/dev/null || { echo "resvg not found (cargo install resvg)" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
set_dir="$work/AppIcon.iconset"
mkdir -p "$set_dir"

pad() { sed 's/viewBox="0 0 1024 1024"/viewBox="-124 -124 1272 1272"/' "$1" > "$2"; }
pad "$root/design/icon/overtone-app.svg" "$work/large.svg"
pad "$root/design/icon/overtone-app-small.svg" "$work/small.svg"

render() { resvg -w "$2" -h "$2" "$1" "$set_dir/$3"; }
for size in 16 32; do
  render "$work/small.svg" $size "icon_${size}x${size}.png"
  render "$work/small.svg" $((size * 2)) "icon_${size}x${size}@2x.png"
done
for size in 128 256 512; do
  render "$work/large.svg" $size "icon_${size}x${size}.png"
  render "$work/large.svg" $((size * 2)) "icon_${size}x${size}@2x.png"
done

iconutil -c icns "$set_dir" -o "$root/apps/macos/Resources/AppIcon.icns"
echo "icon: $root/apps/macos/Resources/AppIcon.icns"
