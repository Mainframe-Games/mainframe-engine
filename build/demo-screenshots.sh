#!/usr/bin/env bash
# Captures one screenshot per Demo scene into docs/images/demo (README Showcase). Usage: build/demo-screenshots.sh [frames]
set -euo pipefail
frames="${1:-240}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/docs/images/demo"
mkdir -p "$out"
dotnet build "$repo/Examples/Demo/Demo.slnx" -c Release -v q -nologo
bin="$repo/Examples/Demo/Demo.Launcher/bin/Release/net10.0"
for scene in basic_3d basic_2d audio_2d audio_3d ui physics_2d physics_3d spine; do
  dotnet "$bin/Demo.Launcher.dll" --scene "Content/Scenes/$scene.mscene" --fixed-fps 60 --max-frames "$frames" \
    --no-log-file --screenshot "$out/$scene.png"
done
ls -la "$out"
