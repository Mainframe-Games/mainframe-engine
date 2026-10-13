#!/usr/bin/env bash
# Regenerates Tests/QA/Fixtures/video/sample.mp4: 12 s of 320x180 test pattern with a 440 Hz tone that drops out from
# 4 to 6 s (silence to detect). Its sidecar (committed beside it, hand-written) marks 7-9 s as loading and a marker at
# 10 s. Small enough to commit.
set -euo pipefail
out="$(cd "$(dirname "$0")/.." && pwd)/Tests/QA/Fixtures/video/sample.mp4"
mkdir -p "$(dirname "$out")"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=s=320x180:r=30:d=12" \
  -f lavfi -i "sine=f=440:r=48000:d=12" \
  -af "volume=enable='between(t,4,6)':volume=0" \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -g 30 -c:a aac -b:a 64k -movflags +faststart -shortest "$out"
ls -l "$out"
