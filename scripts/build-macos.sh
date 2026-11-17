#!/usr/bin/env bash
# Builds Overtone.app: Rust core, mediaremote-adapter, Swift app, bundle, signature.
#
# Usage: scripts/build-macos.sh [--debug] [--universal] [--skip-core] [--skip-adapter]
# Output: apps/macos/build/Overtone.app
#
# Signing: ad hoc by default (runs on this Mac). For distribution set
# CODESIGN_IDENTITY="Developer ID Application: …"; the hardened runtime is
# enabled then, ready for notarisation.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
app_src="$root/apps/macos"
config=release
core_args=()
skip_core=0
skip_adapter=0
universal=0
for arg in "$@"; do
  case "$arg" in
    --debug) config=debug; core_args+=(--debug) ;;
    --universal) universal=1; core_args+=(--universal) ;;
    --skip-core) skip_core=1 ;;
    --skip-adapter) skip_adapter=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

[[ $skip_core == 1 ]] || "$root/scripts/build-core-macos.sh" ${core_args[@]+"${core_args[@]}"}
adapter_args=()
[[ $universal == 1 ]] && adapter_args+=(--universal)
[[ $skip_adapter == 1 && -d "$app_src/build/adapter" ]] || "$root/scripts/build-adapter-macos.sh" ${adapter_args[@]+"${adapter_args[@]}"}

swift_args=(-c "$config" --package-path "$app_src" --product Overtone)
if [[ $universal == 1 ]]; then
  # One build per architecture, then lipo: a multi-arch `swift build` goes through
  # XCBuild, which (Xcode 26) fails to link the static core ("-lovertone_core").
  # Each slice is copied out at once: newer SwiftPM builds every triple to the same path.
  bin_dir="$app_src/.build/universal-$config"
  mkdir -p "$bin_dir"
  for arch in arm64 x86_64; do
    triple_args=("${swift_args[@]}" --triple "$arch-apple-macosx14.0")
    swift build "${triple_args[@]}"
    cp "$(swift build "${triple_args[@]}" --show-bin-path)/Overtone" "$bin_dir/Overtone-$arch"
  done
  lipo -create "$bin_dir/Overtone-arm64" "$bin_dir/Overtone-x86_64" -output "$bin_dir/Overtone"
else
  swift build "${swift_args[@]}"
  bin_dir="$(swift build "${swift_args[@]}" --show-bin-path)"
fi

app="$app_src/build/Overtone.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$app/Contents/Frameworks" "$app/Contents/Helpers"
cp "$app_src/Resources/Info.plist" "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
cp "$bin_dir/Overtone" "$app/Contents/MacOS/Overtone"
cp "$app_src/Resources/AppIcon.icns" "$app/Contents/Resources/"

adapter="$app_src/build/adapter"
cp "$adapter/mediaremote-adapter.pl" "$app/Contents/Resources/"
cp "$adapter/LICENSE" "$app/Contents/Resources/mediaremote-adapter-LICENSE.txt"
cp -R "$adapter/MediaRemoteAdapter.framework" "$app/Contents/Frameworks/"
cp "$adapter/MediaRemoteAdapterTestClient" "$app/Contents/Helpers/"

identity="${CODESIGN_IDENTITY:--}"
sign_args=(--force --sign "$identity" --timestamp=none)
if [[ "$identity" != "-" ]]; then
  sign_args=(--force --sign "$identity" --timestamp --options runtime)
fi
# Inside out: nested code first, the app last.
codesign "${sign_args[@]}" "$app/Contents/Frameworks/MediaRemoteAdapter.framework"
codesign "${sign_args[@]}" "$app/Contents/Helpers/MediaRemoteAdapterTestClient"
codesign "${sign_args[@]}" "$app"
codesign --verify --strict "$app"

echo "app: $app"
