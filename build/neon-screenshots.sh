#!/usr/bin/env bash
# Neon's eight reference shots (N1–N8, docs/design/neon.md#reference-shots) into docs/images/neon/<name>.png: each is
# rendered in Release at the window size (1920 × 1080) with a fixed frame rate, from a fixed view (--view) with the
# shot's props switched on, then scaled to 1600 px wide. Needs the display awake (caffeinate -u) and the LFS art
# (git lfs pull).
# Usage: build/neon-screenshots.sh [frames] [game args...]
#   NEON_SHOTS="1 4"       only these shots (numbers, default all eight)
#   NEON_SHOTS_OUT=<dir>   write there instead of docs/images/neon (before/after sets)
set -euo pipefail
frames="${1:-240}"
shift || true
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${NEON_SHOTS_OUT:-$repo/docs/images/neon}"
mkdir -p "$out"
dotnet build "$repo/Examples/Neon/Neon.slnx" -c Release -v q -nologo
bin="$repo/Examples/Neon/Neon.Desktop/bin/Release/net10.0"
# name | feet x,y,z,yaw,pitch | extra game flags
shots=(
  "n1-loft|-2.0,3,4.8,-46,-14|--tv 2"
  "n2-computer|-2.3,0,4.4,60,-8|--open-doors --music"
  "n3-shower|-3.0,0,-2.3,53,-6|--open-doors --shower"
  "n4-balcony|0,0,7.4,180,-32|--open-doors"
  "n5-skyline|5.2,0,7.5,160,6|--open-doors"
  "n6-bedroom|-3.6,3,-2.0,49,-4|--open-doors"
  "n7-kitchen|4.0,0,-1.0,32,-10|--tv 1"
  "n8-windows|6.0,0,-4.5,145,2|--tv 0"
)
for n in ${NEON_SHOTS:-1 2 3 4 5 6 7 8}; do
  IFS='|' read -r name view flags <<< "${shots[$((n - 1))]}"
  # shellcheck disable=SC2086
  (cd "$bin" && dotnet Neon.Desktop.dll --fixed-fps 60 --max-frames "$frames" --no-log-file --screenshot "$out/$name.png" \
    ++ --no-capture --no-audio --view "$view" $flags "$@")
  if command -v sips > /dev/null; then
    sips --resampleWidth 1600 "$out/$name.png" > /dev/null
  fi
done
ls -la "$out"
