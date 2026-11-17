#!/usr/bin/env bash
# Packs Overtone.app into a drag-to-Applications disk image.
#
# Usage: scripts/package-dmg-macos.sh <Overtone.app> <output.dmg>
# Build the app first: scripts/build-macos.sh
set -euo pipefail

app="${1:?usage: package-dmg-macos.sh <Overtone.app> <output.dmg>}"
dmg="${2:?usage: package-dmg-macos.sh <Overtone.app> <output.dmg>}"
[[ -d "$app" ]] || { echo "not found: $app" >&2; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
ditto "$app" "$stage/Overtone.app"
ln -s /Applications "$stage/Applications"

mkdir -p "$(dirname "$dmg")"
rm -f "$dmg"
hdiutil create -volname Overtone -srcfolder "$stage" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$dmg" >/dev/null
# Sign the image too when a real identity is set (scripts/build-macos.sh signs the app).
if [[ -n "${CODESIGN_IDENTITY:-}" && "$CODESIGN_IDENTITY" != "-" ]]; then
  codesign --force --sign "$CODESIGN_IDENTITY" --timestamp "$dmg"
fi
echo "dmg: $dmg"
