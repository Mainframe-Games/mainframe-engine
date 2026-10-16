#!/usr/bin/env bash
# Records the Forest twice and checks the results with ffprobe (needs ffmpeg/ffprobe on PATH, display awake, LFS art).
#   offline: a flyover at the project window size, fixed 60 fps. --max-frames counts only loaded frames and the recording
#            also covers loading, so the video has at least 3600 frames (60 s); its audio must match its length
#   live:    10 s of autowalk at window size
# Not passed: --record-scale n (a fixed content scale, e.g. 2 for 4K from the 1920x1080-point window) needs a display large
# enough for the scaled window, or the engine refuses to start; and never Forest's --resolution, which resizes the window
# after load and a resize ends a recording by design.
# Usage: build/forest-record.sh [out-dir]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$repo/artifacts/recordings}"
mkdir -p "$out"
for tool in ffmpeg ffprobe; do
  command -v "$tool" > /dev/null || { echo "$tool must be on PATH"; exit 1; }
done
rm -rf "$out/offline.mp4" "$out/offline.mp4.mfcap.json" "$out/offline.mp4.partial" \
  "$out/live.mp4" "$out/live.mp4.mfcap.json" "$out/live.mp4.partial"
dotnet build "$repo/Examples/Forest/Forest.slnx" -c Release -v q -nologo
bin="$repo/Examples/Forest/Forest.Desktop/bin/Release/net10.0"

(cd "$bin" && dotnet Forest.Desktop.dll --fixed-fps 60 --max-frames 3600 --no-log-file \
  --record "$out/offline.mp4" ++ --camera flyover --no-capture)
(cd "$bin" && dotnet Forest.Desktop.dll --max-frames 600 --no-log-file \
  --record "$out/live.mp4" ++ --autowalk --no-capture)

for f in offline live; do
  test -f "$out/$f.mp4" || { echo "missing $out/$f.mp4"; exit 1; }
  test -f "$out/$f.mp4.mfcap.json" || { echo "missing $out/$f.mp4.mfcap.json"; exit 1; }
done

frames=$(ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 "$out/offline.mp4")
size=$(ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 "$out/offline.mp4")
audio=$(ffprobe -v error -select_streams a:0 -show_entries stream=duration -of csv=p=0 "$out/offline.mp4")
echo "offline: $frames frames, $size, audio ${audio}s (round(audio x 60) = $(awk -v a="$audio" 'BEGIN { printf "%d", a * 60 + 0.5 }'))"
# mode, duration and how many loading spans the sidecar has (empty when the recording started after the load: informational)
sidecar() { python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["mode"], d["durationSeconds"], len(d["loading"]), sum(s["end"]-s["start"] for s in d["loading"]), "%dx%d" % (d["width"], d["height"]))' "$out/$1.mp4.mfcap.json"; }
read -r mode duration spans seconds sidecarsize < <(sidecar offline)
echo "offline sidecar: mode $mode, duration ${duration}s, ${spans} loading span(s) totalling ${seconds}s, ${sidecarsize}"

# --max-frames counts loaded frames only and the recorder also records loading, so there are at least 3600.
[[ "$frames" =~ ^[0-9]+$ ]] || { echo "ffprobe gave no frame count ('$frames')"; exit 1; }
((frames >= 3600)) || { echo "expected at least 3600 frames, got $frames"; exit 1; }
# Video and audio come out the same length: frames == round(audio seconds x 60), give or take one.
awk -v f="$frames" -v a="$audio" 'BEGIN { d = f - int(a * 60 + 0.5); exit (d >= -1 && d <= 1) ? 0 : 1 }' ||
  { echo "audio ${audio}s does not match $frames frames at 60 fps"; exit 1; }
[[ "$size" == "$sidecarsize" ]] || { echo "video is $size but the sidecar says $sidecarsize"; exit 1; }
[[ "$mode" == "offline" ]] || { echo "offline sidecar says mode '$mode'"; exit 1; }
read -r livemode liveduration livespans _ _ < <(sidecar live)
echo "live sidecar: mode $livemode, duration ${liveduration}s, ${livespans} loading span(s)"
[[ "$livemode" == "live" ]] || { echo "live sidecar says mode '$livemode'"; exit 1; }
echo "ok: $out"
