#!/usr/bin/env bash
# Demo smoke test (CI job "template"; run it locally the same way): builds Examples/Demo, runs its tests, then runs every
# demo scene headless for a few frames, saving the last frame of each to <work-dir>/<scene>.png. Fails on any build or
# test error, a non-zero exit code, a missing screenshot or an [ERROR]/[FATAL] line in a scene's log.
#
# usage: build/demo-smoke.sh <work-dir> [frames]
# env:   CONFIGURATION (Release), BUILD_ARGS (extra dotnet build arguments)
set -euo pipefail

work="${1:?usage: build/demo-smoke.sh <work-dir> [frames]}"
frames="${2:-10}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
config="${CONFIGURATION:-Release}"

# shellcheck disable=SC2086 # BUILD_ARGS is a list of arguments
dotnet build "$repo/Examples/Demo/Demo.slnx" -c "$config" -warnaserror ${BUILD_ARGS:-}
dotnet test "$repo/Examples/Demo/Demo.Tests" -c "$config" --no-build

bin="$repo/Examples/Demo/Demo.Launcher/bin/$config/net10.0"
rm -rf "$work"
mkdir -p "$work"
failed=0
for scene in basic_3d basic_2d audio_2d audio_3d ui physics_2d physics_3d spine; do
  # One user-data folder per scene: each run rotates the previous run's log away.
  userdata="$work/userdata/$scene"
  MAINFRAME_USER_DATA="$userdata" dotnet "$bin/Demo.Launcher.dll" --scene "Content/Scenes/$scene.mscene" \
    --max-frames "$frames" --hidden --fixed-fps 60 --no-vsync --screenshot "$work/$scene.png"
  if [ ! -s "$work/$scene.png" ]; then
    echo "demo-smoke: $scene produced no screenshot" >&2
    failed=1
  fi
  # The log is {user data}/{game name}/logs/{game name}.log (the name is the project's, "Mainframe Demo").
  log="$(find "$userdata" -name '*.log' -not -name '*.*.log' | head -n 1)"
  if [ -z "$log" ] || [ ! -s "$log" ]; then
    echo "demo-smoke: $scene wrote no log under $userdata" >&2
    failed=1
  elif grep -E '\[(ERROR|FATAL)\]' "$log"; then
    echo "demo-smoke: $scene logged errors (above)" >&2
    failed=1
  fi
done
if [ "$failed" -ne 0 ]; then exit 1; fi
echo "demo-smoke: every Demo scene ran $frames frames"
