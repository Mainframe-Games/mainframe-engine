#!/usr/bin/env bash
# Converts one PNG (ideally 1024 px, at least 512) into a macOS .icns: sips scales it into an iconset, iconutil packs it.
# Usage: build/macos/png-to-icns.sh <icon.png> <out.icns>   (macOS only: sips and iconutil ship with the OS)
# Used by build/macos/DevAppBundle.targets (a game's window.icon) and build/package-game.sh.
set -euo pipefail
png="$1"; out="$2"
set_dir="$(mktemp -d)/icon.iconset"
trap 'rm -rf "$(dirname "$set_dir")"' EXIT
mkdir -p "$set_dir" "$(dirname "$out")"
for s in 16 32 128 256 512; do
  sips -z "$s" "$s" "$png" --out "$set_dir/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) "$png" --out "$set_dir/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$set_dir" -o "$out"
