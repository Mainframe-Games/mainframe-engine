#!/usr/bin/env bash
# Renders a game's app icon set from its SVG sources (the same workflow as `just brand` for the engine logo).
# Usage: build/brand/app-icon.sh <brand-dir> <name>
#
# <brand-dir> holds the sources:
#   icon.svg         master artwork, full-bleed tile (64 px and above)
#   icon-small.svg   simplified artwork (48 px and below)
#   icon-macos.svg   icon.svg on Apple's app-icon grid (1024 canvas, 824 px tile, 100 px transparent margin)
# and receives:
#   png/icon-{16,24,32,48}.png        from icon-small.svg
#   png/icon-{64,128,256,512,1024}.png  from icon.svg
#   png/icon-macos-{16…1024}.png      from icon-macos.svg (the .icns iconset)
#   <name>.ico                        16–256, 7 sizes (Windows ApplicationIcon)
#   <name>.icns                       macOS only (iconutil); the app bundle's CFBundleIconFile (MacAppIcon)
# Needs Inkscape; the .ico is packed by build/brand/make-ico.py (no dependencies).
set -euo pipefail
dir="$(cd "$1" && pwd)"; name="$2"
here="$(cd "$(dirname "$0")" && pwd)"
cd "$dir"
mkdir -p png
for n in 16 24 32 48; do inkscape icon-small.svg -w $n -h $n -o png/icon-$n.png; done
for n in 64 128 256 512 1024; do inkscape icon.svg -w $n -h $n -o png/icon-$n.png; done
python3 "$here/make-ico.py" "$name.ico" png/icon-{16,24,32,48,64,128,256}.png
for n in 16 32 64 128 256 512 1024; do inkscape icon-macos.svg -w $n -h $n -o png/icon-macos-$n.png; done
if command -v iconutil >/dev/null; then
  set_dir="$(mktemp -d)/icon.iconset"; mkdir -p "$set_dir"
  for n in 16 32 128 256 512; do
    cp png/icon-macos-$n.png "$set_dir/icon_${n}x${n}.png"
    cp png/icon-macos-$((n * 2)).png "$set_dir/icon_${n}x${n}@2x.png"
  done
  iconutil -c icns "$set_dir" -o "$name.icns"
  rm -rf "$(dirname "$set_dir")"
else
  echo "iconutil not found (macOS only): $name.icns left unchanged"
fi
