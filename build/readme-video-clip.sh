#!/usr/bin/env bash
# The recording the README's video-mode screenshot edits (just readme-screenshots): the Demo's Physics 2D scene recorded
# offline (--record --fixed-fps 60, 15.5 s; the bodies spawn at random spots, but every run has them pouring through the
# pegs at 1-4 s), its audio replaced by a short synthesised narration with two pauses and two filler words for Detect
# Silence and Remove Fillers to cut. Writes <dir>/demo.mp4 and its .mfcap.json sidecar. Needs ffmpeg and a speech
# synthesiser: `say` (macOS) or `espeak-ng` (Linux).
# Usage: build/readme-video-clip.sh <dir>
set -euo pipefail
dir="$1"
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$dir/.clip"
rm -rf "$work"
mkdir -p "$work"

dotnet build "$repo/Examples/Demo/Demo.slnx" -c Release -v q -nologo
bin="$repo/Examples/Demo/Demo.Desktop/bin/Release/net10.0"
(cd "$repo/Examples/Demo/Demo.Desktop" && dotnet "$bin/Demo.Desktop.dll" --scene Assets/Scenes/physics_2d.mscene \
  --fixed-fps 60 --max-frames 900 --no-log-file --record "$work/gameplay.mp4")

# The first phrase covers the bodies' fall (the screenshot stops at 2.8 s), the first pause the pile-up.
if command -v say > /dev/null; then
  say -o "$work/narration.aiff" "[[slnc 400]] Here's the 2D physics demo: forty bodies rain down through the pegs. \
[[slnc 2000]] Um, they pile up at the bottom, [[slnc 700]] and, uh, it all settles in seconds. [[slnc 1600]] Ready to play."
  narration="$work/narration.aiff"
elif command -v espeak-ng > /dev/null; then
  # espeak-ng has no inline pauses: speak each phrase and join them with silences of the same lengths.
  phrases=("Here's the 2D physics demo: forty bodies rain down through the pegs." "Um, they pile up at the bottom," \
    "and, uh, it all settles in seconds." "Ready to play.")
  gaps=(2.0 0.7 1.6)
  inputs=(-f lavfi -t 0.4 -i anullsrc=r=22050:cl=mono)
  filter="[0]"
  for i in "${!phrases[@]}"; do
    espeak-ng -w "$work/p$i.wav" "${phrases[$i]}"
    inputs+=(-i "$work/p$i.wav")
    filter+="[$((2 * i + 1))]"
    if ((i < ${#gaps[@]})); then
      inputs+=(-f lavfi -t "${gaps[$i]}" -i anullsrc=r=22050:cl=mono)
      filter+="[$((2 * i + 2))]"
    fi
  done
  ffmpeg -v error -y "${inputs[@]}" -filter_complex "${filter}concat=n=$((2 * ${#phrases[@]})):v=0:a=1" "$work/narration.wav"
  narration="$work/narration.wav"
else
  echo "readme-video-clip: needs 'say' (macOS) or 'espeak-ng' for the narration" >&2
  exit 1
fi

duration=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$work/gameplay.mp4")
ffmpeg -v error -y -i "$work/gameplay.mp4" -i "$narration" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 160k \
  -af apad -t "$duration" -movflags +faststart "$dir/demo.mp4"
sed 's/"video": "gameplay.mp4"/"video": "demo.mp4"/' "$work/gameplay.mp4.mfcap.json" > "$dir/demo.mp4.mfcap.json"
rm -rf "$work"
