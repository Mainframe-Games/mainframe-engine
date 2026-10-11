#!/usr/bin/env bash
# Saves and settings QA (G4) against the real Demo: rebind a key through the settings menu and change settings, restart
# and check they came back from settings.json, then damage a save and check it is recovered from its backup. The user
# data lives in artifacts/qa-settings (fresh every run). Usage: build/qa-demo-settings.sh
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/artifacts/qa-settings"
rm -rf "$out" && mkdir -p "$out"
export MAINFRAME_USER_DATA="$out/user"
dotnet build "$repo/Examples/Demo/Demo.slnx" -c Release -v q -nologo
bin="$repo/Examples/Demo/Demo.Desktop/bin/Release/net10.0"
run() {
  local mode="$1" scene="$2"
  echo "== qa $mode"
  dotnet "$bin/Demo.Desktop.dll" --scene "Content/Scenes/$scene.mscene" --hidden --fixed-fps 60 --max-frames 600 \
    --no-log-file ++ --qa "$mode" 2>&1 | tee "$out/$mode.log" | grep -E "QA: |ERROR" || true
  grep -q "QA: $mode: PASS" "$out/$mode.log" || { echo "qa $mode FAILED (see $out/$mode.log)"; exit 1; }
}
run settings physics_3d
run check physics_3d
run saves physics_3d
echo "Saves and settings QA passed: $out"
