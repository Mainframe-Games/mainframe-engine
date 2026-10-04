#!/usr/bin/env bash
# Template smoke test (CI job "template", `just template-smoke`): installs the mfgame template from this checkout into
# a private template hive, creates SmokeGame against this engine (--engine-path), builds it with warnings as errors and
# runs its GameHost launcher for a few frames with a hidden window, saving the last frame to <work-dir>/screenshot.png.
# Fails on any build error, a non-zero exit code, a missing screenshot or an [ERROR]/[FATAL] line in the game's log.
#
# usage: build/template-smoke.sh [work-dir] [frames]
# env:   CONFIGURATION (Debug), BUILD_ARGS (extra dotnet build arguments), SKIP_RUN=1 (build only)
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
work="${1:-$repo/artifacts/template-smoke}"
frames="${2:-30}"
config="${CONFIGURATION:-Debug}"

rm -rf "$work"
mkdir -p "$work"
hive="$work/hive"

dotnet new install "$repo/Templates/MainframeEngine.Templates/content/mfgame" --debug:custom-hive "$hive"
dotnet new mfgame -n SmokeGame -o "$work/SmokeGame" --engine-path "$repo" --debug:custom-hive "$hive"

# shellcheck disable=SC2086 # BUILD_ARGS is a list of arguments
dotnet build "$work/SmokeGame/SmokeGame.slnx" -c "$config" -warnaserror ${BUILD_ARGS:-}

out="$work/SmokeGame/SmokeGame.Launcher/bin/$config/net10.0"
for required in SmokeGame.Launcher.dll SmokeGame.dll project.mfproj Content/Scenes/Main.mscene; do
  if [ ! -e "$out/$required" ]; then
    echo "template-smoke: $required is missing from $out" >&2
    exit 1
  fi
done

if [ "${SKIP_RUN:-0}" = "1" ]; then
  echo "template-smoke: built (run skipped)"
  exit 0
fi

# Logs go to a throwaway user-data folder, not the runner's home.
MAINFRAME_USER_DATA="$work/userdata" dotnet "$out/SmokeGame.Launcher.dll" \
  --max-frames "$frames" --hidden --fixed-fps 60 --no-vsync --screenshot "$work/screenshot.png"

log="$work/userdata/SmokeGame/logs/SmokeGame.log"
if [ ! -s "$log" ]; then
  echo "template-smoke: no log file at $log" >&2
  exit 1
fi
if grep -E '\[(ERROR|FATAL)\]' "$log"; then
  echo "template-smoke: the game logged errors (above)" >&2
  exit 1
fi
if [ ! -s "$work/screenshot.png" ]; then
  echo "template-smoke: no screenshot at $work/screenshot.png" >&2
  exit 1
fi
echo "template-smoke: SmokeGame built and ran $frames frames"
