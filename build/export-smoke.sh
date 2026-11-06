#!/usr/bin/env bash
# Packed export smoke test (`just export-smoke`, G19, docs/design/game-export.md#packed-content): captures the Demo's
# scenes from its Release build (loose files), exports the Demo for this machine with mf-export (packed into
# Assets.mfpak and encrypted, the defaults), runs the export on the same scenes and compares each pair of screenshots with ImageMagick.
# Fails when the export ships an Assets/ folder or a loose project.mfproj, logs an error, misses a screenshot, or a
# screenshot differs by more than the threshold (RMSE, 0-1). The physics, particle and tile scenes differ between runs of
# the same loose build (their simulations are not run-to-run deterministic), so they are run and checked for errors
# but not compared.
# Needs the display awake on macOS (caffeinate -u) and `magick`. Outputs in artifacts/export-smoke.
#
# usage: build/export-smoke.sh [frames] [threshold]
set -euo pipefail
frames="${1:-120}"
threshold="${2:-0.01}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$repo/artifacts/export-smoke"
rm -rf "$work"
mkdir -p "$work/loose" "$work/packed" "$work/logs"
command -v magick > /dev/null || { echo "export-smoke: needs ImageMagick (magick)" >&2; exit 1; }

case "$(uname -s)-$(uname -m)" in
  Darwin-x86_64) rid=osx-x64 ;;
  Darwin-*) rid=osx-arm64 ;;
  Linux-*) rid=linux-x64 ;;
  *) rid=win-x64 ;;
esac
scenes=(basic_3d basic_2d ui physics_2d physics_3d spine particles animation_3d shaders sprites_2d tiles_2d paths_3d)
unstable=" physics_2d physics_3d particles tiles_2d "
args() { echo --scene "Assets/Scenes/$1.mfscene" --fixed-fps 60 --max-frames "$frames" --no-log-file; }

# The loose build.
dotnet build "$repo/Examples/Demo/Demo.Desktop" -c Release -v q -nologo
bin="$repo/Examples/Demo/Demo.Desktop/bin/Release/net10.0"
for scene in "${scenes[@]}"; do
  # shellcheck disable=SC2046 # args is a word list
  dotnet "$bin/Demo.Desktop.dll" $(args "$scene") --screenshot "$work/loose/$scene.png" > "$work/logs/loose-$scene.log" 2>&1
done

# The packed export (the old package-game form: an unsaved preset with every default, so pack is on).
exported="$(dotnet run --project "$repo/Tools/MainframeEngine.Export.Cli" -c Release -- \
  "$repo/Examples/Demo/Demo.Desktop/Demo.Desktop.csproj" "$rid" "$work/export" --folder --exe DemoSmoke | tail -n 1)"
case "$rid" in
  osx-*) app="$(find "$exported" -maxdepth 1 -name '*.app' | head -n 1)"; content="$app/Contents/MonoBundle" exe="$app/Contents/MacOS/DemoSmoke" ;;
  win-*) content="$exported" exe="$exported/DemoSmoke.exe" ;;
  *) content="$exported" exe="$exported/DemoSmoke" ;;
esac
[ -f "$content/Assets.mfpak" ] || { echo "export-smoke: no Assets.mfpak in $content" >&2; exit 1; }
for loose in Assets project.mfproj; do
  [ ! -e "$content/$loose" ] || { echo "export-smoke: the export ships $loose loose" >&2; exit 1; }
done

failed=0
compared=0
for scene in "${scenes[@]}"; do
  log="$work/logs/packed-$scene.log"
  # shellcheck disable=SC2046 # args is a word list
  MAINFRAME_USER_DATA="$work/userdata" "$exe" $(args "$scene") --screenshot "$work/packed/$scene.png" > "$log" 2>&1
  if grep -E '\[(ERROR|FATAL)\]' "$log"; then
    echo "export-smoke: $scene logged errors (above, $log)" >&2
    failed=1
  fi
  if ! grep -q "Content from Assets.mfpak (encrypted)" "$log"; then
    echo "export-smoke: $scene did not read Assets.mfpak ($log)" >&2
    failed=1
  fi
  if [ ! -s "$work/packed/$scene.png" ] || [ ! -s "$work/loose/$scene.png" ]; then
    echo "export-smoke: $scene has no screenshot" >&2
    failed=1
    continue
  fi
  if [[ "$unstable" == *" $scene "* ]]; then
    echo "export-smoke: $scene ran from the pack (not compared: differs run to run)"
    continue
  fi
  # RMSE prints "absolute (normalised)" to stderr and exits 1 when the images differ at all.
  rmse="$(magick compare -metric RMSE "$work/loose/$scene.png" "$work/packed/$scene.png" "$work/packed/$scene.diff.png" 2>&1 || true)"
  normalised="$(sed -E 's/.*\(([0-9.e+-]+)\).*/\1/' <<<"$rmse")"
  if awk -v d="$normalised" -v t="$threshold" 'BEGIN { exit !(d > t) }'; then
    echo "export-smoke: $scene differs from the loose build (RMSE $normalised > $threshold; $work/packed/$scene.diff.png)" >&2
    failed=1
  else
    echo "export-smoke: $scene matches (RMSE $normalised)"
    compared=$((compared + 1))
  fi
done
[ "$failed" = 0 ] || exit 1
echo "export-smoke: the packed export of the Demo ran ${#scenes[@]} scenes from its pack; $compared matched the loose build's screenshots"
