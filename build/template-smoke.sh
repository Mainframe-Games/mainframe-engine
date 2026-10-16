#!/usr/bin/env bash
# Template smoke test (CI job "template", `just template-smoke`, `just template-smoke-package`): installs the mfgame
# template from this checkout into a private template hive, creates SmokeGame, builds it with warnings as errors and
# runs its desktop project (GameHost) for a few frames with a hidden window, saving the last frame to <work-dir>/screenshot.png.
# Fails on any build error, a non-zero exit code, missing engine content, catalogs or BRUTAL-NOTICE.txt, a missing
# screenshot or an [ERROR]/[FATAL] line in the game's log.
#
# ENGINE_SOURCE=project (default): SmokeGame references this checkout (--engine-source project --engine-path).
# ENGINE_SOURCE=package: this checkout is packed (build/pack-nuget.sh) into a local feed as 0.0.0-smoke.<timestamp>,
# the template is installed from that MainframeEngine.Templates package and SmokeGame is created with its defaults, so
# it references the MainframeEngine package of the same version, as a game made outside the repo does; it is then also
# published for the host RID (natives and content in the publish folder) and packaged for players for linux-x64, win-x64
# and osx-arm64 with the package's tools/package-game.sh (archive contents, Steam files included).
#
# usage: build/template-smoke.sh [work-dir] [frames]
# env:   ENGINE_SOURCE (project|package), CONFIGURATION (Debug), BUILD_ARGS (extra dotnet build/pack arguments),
#        SKIP_RUN=1 (build only)
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
work="${1:-$repo/artifacts/template-smoke}"
frames="${2:-30}"
config="${CONFIGURATION:-Debug}"
source="${ENGINE_SOURCE:-project}"

rm -rf "$work"
mkdir -p "$work"
hive="$work/hive"

case "$source" in
  project)
    dotnet new install "$repo/Templates/MainframeEngine.Templates/content/mfgame" --debug:custom-hive "$hive"
    dotnet new mfgame -n SmokeGame -o "$work/SmokeGame" --engine-source project --engine-path "$repo" --debug:custom-hive "$hive"
    ;;
  package)
    # A version of its own per run, so a package cache never serves an older build of the same version; removed from
    # the cache again on exit.
    version="0.0.0-smoke.$(date +%s)"
    packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
    trap 'rm -rf "$packages/mainframeengine/$version"' EXIT
    PACK_ARGS="${BUILD_ARGS:-}" "$repo/build/pack-nuget.sh" "$version" "$work/feed"
    dotnet new install "$work/feed/MainframeEngine.Templates.$version.nupkg" --debug:custom-hive "$hive"
    dotnet new mfgame -n SmokeGame -o "$work/SmokeGame" --debug:custom-hive "$hive"
    if ! grep -q "<MainframeEngineVersion>$version</MainframeEngineVersion>" "$work/SmokeGame/Directory.Build.props"; then
      echo "template-smoke: the packed template does not default to MainframeEngine $version:" >&2
      cat "$work/SmokeGame/Directory.Build.props" >&2
      exit 1
    fi
    cat > "$work/SmokeGame/nuget.config" <<NUGET
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$work/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
NUGET
    ;;
  *) echo "template-smoke: ENGINE_SOURCE must be project or package, not '$source'" >&2; exit 1 ;;
esac

# A catalog, so the build's .po -> .mo step (Localization.targets, from the checkout or the package) runs too.
mkdir -p "$work/SmokeGame/Content/locale/fr/LC_MESSAGES"
cat > "$work/SmokeGame/Content/locale/fr/LC_MESSAGES/messages.po" <<'PO'
msgid ""
msgstr ""
"Content-Type: text/plain; charset=UTF-8\n"
"Language: fr\n"
"Plural-Forms: nplurals=2; plural=(n > 1);\n"

msgid "Hello"
msgstr "Bonjour"
PO

# shellcheck disable=SC2086 # BUILD_ARGS is a list of arguments
dotnet build "$work/SmokeGame/SmokeGame.slnx" -c "$config" -warnaserror ${BUILD_ARGS:-}

out="$work/SmokeGame/SmokeGame.Desktop/bin/$config/net10.0"
# The game's files, then the engine's content (copied by the engine project or the package) and the compiled catalog.
require() {
  for required in "${@:2}"; do
    if [ ! -e "$1/$required" ]; then
      echo "template-smoke: $required is missing from $1" >&2
      exit 1
    fi
  done
}
engine_files=(Content/UI/widgets/widgets.rcss Content/Shaders/Mesh/Mesh.vk.vert.spv Content/locale/fr/LC_MESSAGES/messages.mo BRUTAL-NOTICE.txt)
require "$out" SmokeGame.Desktop.dll SmokeGame.dll project.mfproj Content/Scenes/Main.mscene Content/icon.png "${engine_files[@]}"
# Steamworks: both Facepunch builds behind the engine's shim (the engine loads this OS's) and every steam_api native.
steam_files=(steamworks/win64/MainframeEngine.Steamworks.dll steamworks/win64/Facepunch.Steamworks.Win64.dll
  steamworks/posix/MainframeEngine.Steamworks.dll steamworks/posix/Facepunch.Steamworks.Posix.dll)
require "$out" "${steam_files[@]}"

# Package games publish per RID: NuGet copies that RID's natives, the package's targets the engine content.
if [ "$source" = "package" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-*) rid=osx-arm64 rmlui=libmfrmlui.dylib ;;
    Linux-*) rid=linux-x64 rmlui=libmfrmlui.so ;;
    *) rid=win-x64 rmlui=mfrmlui.dll ;;
  esac
  # shellcheck disable=SC2086 # BUILD_ARGS is a list of arguments
  dotnet publish "$work/SmokeGame/SmokeGame.Desktop/SmokeGame.Desktop.csproj" -c "$config" -r "$rid" --self-contained false \
    -o "$work/publish" -warnaserror ${BUILD_ARGS:-}
  case "$rid" in
    win-*) steam_variant=win64 ;;
    *) steam_variant=posix ;;
  esac
  require "$work/publish" "$rmlui" SmokeGame.Desktop.dll project.mfproj Content/Scenes/Main.mscene "${engine_files[@]}" \
    "steamworks/$steam_variant/MainframeEngine.Steamworks.dll"

  # Player builds (#48): the package's tools/package-game.sh, found through MainframePackageGameScript, makes the three
  # archives a checkout makes. Cross-RID publishes work from any host; the .icns needs macOS.
  desktop="$work/SmokeGame/SmokeGame.Desktop/SmokeGame.Desktop.csproj"
  script="$(dotnet msbuild "$desktop" -getProperty:MainframePackageGameScript)"
  case "$script" in
    */mainframeengine/"$version"/tools/package-game.sh) ;;
    *) echo "template-smoke: MainframePackageGameScript is '$script', not the package's tools/package-game.sh" >&2; exit 1 ;;
  esac
  # Lists an archive and checks entries: +path must be there, -path must not.
  check_archive() {
    local archive="$1" listing entry; shift
    case "$archive" in
      *.zip) listing="$(unzip -Z1 "$archive")" ;;
      *) listing="$(tar -tzf "$archive")" ;;
    esac
    for entry in "$@"; do
      case "$entry" in
        +*) grep -qxF "${entry:1}" <<<"$listing" || { echo "template-smoke: ${entry:1} is missing from $archive" >&2; exit 1; } ;;
        -*) ! grep -qF "${entry:1}" <<<"$listing" || { echo "template-smoke: $archive must not contain ${entry:1}" >&2; exit 1; } ;;
      esac
    done
  }
  for package_rid in linux-x64 win-x64 osx-arm64; do
    archive="$(bash "$script" "$desktop" "$package_rid" "$work/packages" | tail -n 1)"
    base="$(basename "$archive")"; base="${base%.zip}"; base="${base%.tar.gz}"
    case "$package_rid" in
      linux-*) check_archive "$archive" "+$base/SmokeGame" "+$base/libsteam_api.so" "+$base/libmfrmlui.so" \
        "+$base/steamworks/posix/MainframeEngine.Steamworks.dll" "+$base/steamworks/posix/Facepunch.Steamworks.Posix.dll" \
        "+$base/Content/Scenes/Main.mscene" "+$base/BRUTAL-NOTICE.txt" "-steamworks/win64" "-steam_api64.dll" "-steam_appid.txt" ;;
      win-*) check_archive "$archive" "+$base/SmokeGame.exe" "+$base/steam_api64.dll" "+$base/mfrmlui.dll" \
        "+$base/steamworks/win64/MainframeEngine.Steamworks.dll" "+$base/steamworks/win64/Facepunch.Steamworks.Win64.dll" \
        "+$base/BRUTAL-NOTICE.txt" "-steamworks/posix" "-libsteam_api" "-steam_appid.txt" ;;
      osx-*)
        macos="SmokeGame.app/Contents/MacOS"
        check_archive "$archive" "+SmokeGame.app/Contents/Info.plist" "+$macos/SmokeGame" "+$macos/libsteam_api.dylib" \
          "+$macos/libmfrmlui.dylib" "+$macos/steamworks/posix/MainframeEngine.Steamworks.dll" \
          "+$macos/steamworks/posix/Facepunch.Steamworks.Posix.dll" "+$macos/BRUTAL-NOTICE.txt" "-steamworks/win64" "-steam_api64.dll" "-steam_appid.txt"
        if [ "$(uname -s)" = "Darwin" ]; then
          check_archive "$archive" "+SmokeGame.app/Contents/Resources/icon.icns"
        fi
        ;;
    esac
    echo "template-smoke: packaged $archive"
  done
fi

# macOS: the development app bundle named after the game, with the icon from window.icon (ADR 0182).
if [ "$(uname -s)" = "Darwin" ]; then
  app="$out/SmokeGame.app/Contents"
  plist_value() { /usr/libexec/PlistBuddy -c "Print :$1" "$app/Info.plist"; }
  if [ "$(plist_value CFBundleName)" != "SmokeGame" ] || [ "$(plist_value CFBundleExecutable)" != "SmokeGame.Desktop" ] ||
     [ "$(plist_value CFBundleIdentifier)" != "com.mainframegames.smokegame.dev" ] || [ "$(plist_value CFBundleIconFile)" != "icon.icns" ]; then
    echo "template-smoke: unexpected $app/Info.plist:" >&2
    cat "$app/Info.plist" >&2
    exit 1
  fi
  if [ ! -s "$app/Resources/icon.icns" ] || [ ! -x "$app/MacOS/SmokeGame.Desktop" ]; then
    echo "template-smoke: $out/SmokeGame.app is missing its icon or executable" >&2
    exit 1
  fi
fi

if [ "${SKIP_RUN:-0}" = "1" ]; then
  echo "template-smoke: built (run skipped)"
  exit 0
fi

# Logs go to a throwaway user-data folder, not the runner's home.
MAINFRAME_USER_DATA="$work/userdata" dotnet "$out/SmokeGame.Desktop.dll" \
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
