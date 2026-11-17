#!/usr/bin/env bash
# Builds the Rust core for macOS and generates its Swift bindings.
#
# Output (both gitignored):
#   apps/macos/Frameworks/overtone_coreFFI.xcframework   static lib + C header
#   apps/macos/Sources/OvertoneCore/Generated/overtone_core.swift
#
# Usage: scripts/build-core-macos.sh [--debug] [--universal]
#   --universal  arm64 + x86_64 (needs: rustup target add x86_64-apple-darwin aarch64-apple-darwin)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
core="$root/core"
app="$root/apps/macos"
profile=release
universal=0
for arg in "$@"; do
  case "$arg" in
    --debug) profile=debug ;;
    --universal) universal=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done
cargo_profile=()
[[ $profile == release ]] && cargo_profile=(--release)

export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-14.0}"
target_dir="$core/target"
work="$app/build/core"
rm -rf "$work"
mkdir -p "$work/include"

if [[ $universal == 1 ]]; then
  for t in aarch64-apple-darwin x86_64-apple-darwin; do
    cargo build --manifest-path "$core/Cargo.toml" --lib --features uniffi ${cargo_profile[@]+"${cargo_profile[@]}"} --target "$t"
  done
  lipo -create \
    "$target_dir/aarch64-apple-darwin/$profile/libovertone_core.a" \
    "$target_dir/x86_64-apple-darwin/$profile/libovertone_core.a" \
    -output "$work/libovertone_core.a"
  dylib="$target_dir/aarch64-apple-darwin/$profile/libovertone_core.dylib"
else
  cargo build --manifest-path "$core/Cargo.toml" --lib --features uniffi ${cargo_profile[@]+"${cargo_profile[@]}"}
  cp "$target_dir/$profile/libovertone_core.a" "$work/libovertone_core.a"
  dylib="$target_dir/$profile/libovertone_core.dylib"
fi

# Bindings are read from the compiled library's metadata ("library mode"),
# which runs `cargo metadata`, so it must run inside the crate.
(cd "$core" && cargo run --quiet --features bindgen --bin uniffi-bindgen -- \
  generate --library "$dylib" --language swift --out-dir "$work/bindings")

cp "$work/bindings/overtone_coreFFI.h" "$work/include/"
cp "$work/bindings/overtone_coreFFI.modulemap" "$work/include/module.modulemap"

rm -rf "$app/Frameworks/overtone_coreFFI.xcframework"
mkdir -p "$app/Frameworks"
xcodebuild -create-xcframework \
  -library "$work/libovertone_core.a" -headers "$work/include" \
  -output "$app/Frameworks/overtone_coreFFI.xcframework" >/dev/null

mkdir -p "$app/Sources/OvertoneCore/Generated"
cp "$work/bindings/overtone_core.swift" "$app/Sources/OvertoneCore/Generated/"

echo "core: $app/Frameworks/overtone_coreFFI.xcframework"
echo "core: $app/Sources/OvertoneCore/Generated/overtone_core.swift"
