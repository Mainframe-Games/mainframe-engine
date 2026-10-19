#!/usr/bin/env bash
# Captures one screenshot per Demo scene into docs/images/demo (README Showcase). Usage: build/demo-screenshots.sh [frames]
# The game captures its framebuffer, which is 2560x1440 on a HiDPI display, so every shot is scaled to 1280x720: the
# README's grid shows them all at one size.
set -euo pipefail
frames="${1:-240}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/docs/images/demo"
mkdir -p "$out"
dotnet build "$repo/Examples/Demo/Demo.slnx" -c Release -v q -nologo
bin="$repo/Examples/Demo/Demo.Desktop/bin/Release/net10.0"
for scene in basic_3d basic_2d audio_2d audio_3d sound_fx ui physics_2d physics_3d spine; do
  dotnet "$bin/Demo.Desktop.dll" --scene "Assets/Scenes/$scene.mscene" --fixed-fps 60 --max-frames "$frames" \
    --no-log-file --screenshot "$out/$scene.png"
done
# The settings menu (G4) over Physics 3D, opened by the Demo's own `++ --settings` argument.
dotnet "$bin/Demo.Desktop.dll" --scene "Assets/Scenes/physics_3d.mscene" --fixed-fps 60 --max-frames "$frames" \
  --no-log-file --screenshot "$out/settings.png" ++ --settings
if command -v sips > /dev/null; then
  for f in "$out"/*.png; do sips --resampleHeightWidth 720 1280 "$f" > /dev/null; done
elif command -v magick > /dev/null; then
  for f in "$out"/*.png; do magick "$f" -resize '1280x720!' "$f"; done
fi
ls -la "$out"
