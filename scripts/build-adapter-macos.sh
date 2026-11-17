#!/usr/bin/env bash
# Fetches and builds mediaremote-adapter (BSD-3-Clause), the macOS now-playing
# source. See docs/ARCHITECTURE.md §1 for why it exists.
#
# Output (gitignored): apps/macos/build/adapter/
#   mediaremote-adapter.pl          run as /usr/bin/perl <script> <framework> [test client] COMMAND
#   MediaRemoteAdapter.framework    loaded by perl, never linked into the app
#   MediaRemoteAdapterTestClient    lets the app check the adapter still works
#   LICENSE
#
# Usage: scripts/build-adapter-macos.sh [--universal]
#   --universal  arm64 + x86_64
#
# Requires cmake (brew install cmake). Bump ADAPTER_REV deliberately and
# re-test: the adapter's CLI may change between revisions.
set -euo pipefail

archs=""
for arg in "$@"; do
  case "$arg" in
    --universal) archs="arm64;x86_64" ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

ADAPTER_REPO="https://github.com/ungive/mediaremote-adapter.git"
ADAPTER_REV="14eed9389241d06e29f7326c19e9ebc6ec8a5f36"

root="$(cd "$(dirname "$0")/.." && pwd)"
build="$root/apps/macos/build"
src="$build/vendor/mediaremote-adapter"
out="$build/adapter"

command -v cmake >/dev/null || { echo "cmake not found (brew install cmake)" >&2; exit 1; }

if [[ ! -d "$src/.git" ]]; then
  mkdir -p "$build/vendor"
  git clone --quiet "$ADAPTER_REPO" "$src"
fi
if [[ "$(git -C "$src" rev-parse HEAD)" != "$ADAPTER_REV" ]]; then
  git -C "$src" fetch --quiet origin
  git -C "$src" checkout --quiet --detach "$ADAPTER_REV"
fi

# Fresh configure each time, so switching between native and universal takes effect.
rm -rf "$src/build"
cmake -S "$src" -B "$src/build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 \
  ${archs:+"-DCMAKE_OSX_ARCHITECTURES=$archs"} >/dev/null
cmake --build "$src/build" --config Release >/dev/null

rm -rf "$out"
mkdir -p "$out"
cp "$src/bin/mediaremote-adapter.pl" "$out/"
cp -R "$src/build/MediaRemoteAdapter.framework" "$out/"
cp "$src/build/MediaRemoteAdapterTestClient" "$out/"
cp "$src/LICENSE" "$out/LICENSE"

echo "adapter: $out ($ADAPTER_REV)"
