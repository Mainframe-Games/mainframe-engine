#!/usr/bin/env bash
# The Forest's five reference shots (R1–R5, docs/design/forest.md#reference-shots) into docs/images/forest/<name>.png:
# each is rendered in Release at 2560 × 1440 with a fixed frame rate (fixed wind, water and exposure phase), then scaled
# to 1600 × 900. Needs the display awake (caffeinate -u) and the LFS art (git lfs pull).
# Usage: build/forest-screenshots.sh [frames] [game args...]
set -euo pipefail
frames="${1:-300}"
shift || true
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/docs/images/forest"
mkdir -p "$out"
dotnet build "$repo/Examples/Forest/Forest.slnx" -c Release -v q -nologo
bin="$repo/Examples/Forest/Forest.Desktop/bin/Release/net10.0"
names=(r1-glade r2-fall r3-bridge r4-vista r5-floor)
for n in 1 2 3 4 5; do
  name="${names[$((n - 1))]}"
  (cd "$bin" && dotnet Forest.Desktop.dll --fixed-fps 60 --max-frames "$frames" --no-log-file --screenshot "$out/$name.png" \
    ++ --no-capture --no-audio --resolution 2560x1440 --shot "$n" "$@")
  if command -v sips > /dev/null; then
    sips --resampleWidth 1600 "$out/$name.png" > /dev/null
  fi
done
ls -la "$out"
