#!/usr/bin/env bash
# Captures the Forest into docs/images/forest (one shot of the main scene for now; the content wave adds the five
# reference shots, --shot 1..5). Usage: build/forest-screenshots.sh [frames]
set -euo pipefail
frames="${1:-300}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/docs/images/forest"
mkdir -p "$out"
dotnet build "$repo/Examples/Forest/Forest.slnx" -c Release -v q -nologo
bin="$repo/Examples/Forest/Forest.Desktop/bin/Release/net10.0"
dotnet "$bin/Forest.Desktop.dll" --fixed-fps 60 --max-frames "$frames" --no-log-file --screenshot "$out/forest.png" \
  ++ --no-capture
ls -la "$out"
