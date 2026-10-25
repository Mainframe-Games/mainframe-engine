#!/usr/bin/env bash
# Packages a published editor folder into the release archive for one RID.
# Usage: build/package-editor.sh <rid> <version> <publish-dir> <out-dir>
# macOS: Mainframe Editor.app, signed inside out (build/macos/sign-app.sh): ad hoc, or with the Developer ID that
# $MF_SIGN_IDENTITY names (hardened runtime, timestamp; mfplughost with build/macos/mfplughost.entitlements). Not notarized.
# Used by .github/workflows/publish.yml and `just publish-local`.
set -euo pipefail

rid="$1" version="$2" publish_dir="$3" out_dir="$4"
name="MainframeEditor-${version}-${rid}"
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

case "$rid" in
  osx-*)
    # The bundle folder and CFBundleName/CFBundleDisplayName are the app name macOS shows (Dock, menu bar,
    # Finder); the executable keeps its assembly name. Dev builds get the same bundle (build/macos/DevAppBundle.targets).
    # Layout (as build/package-game.sh, issue #105): Contents/MacOS may hold only Mach-O code, so the apphost and the
    # plugin helper go there and the rest of the publish (assemblies, the runtime's and the engine's dylibs, content) in
    # Contents/MonoBundle, which is AppContext.BaseDirectory: an osx publish of the editor writes an apphost that carries
    # "../MonoBundle/MainframeEngine.Editor.dll" (build/macos/BundleAppHost.targets). The icon is in Contents/Resources.
    app_name="Mainframe Editor.app"
    app="$staging/$app_name"
    exe="MainframeEngine.Editor"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/MonoBundle" "$app/Contents/Resources"
    cp -R "$publish_dir"/. "$app/Contents/MonoBundle/"
    mv "$app/Contents/MonoBundle/$exe" "$app/Contents/MacOS/$exe"
    sed -e "s/@NAME@/Mainframe Editor/g" -e "s/@BUNDLE_ID@/com.mainframegames.editor/g" -e "s/@EXECUTABLE@/$exe/g" \
      -e "s/@ICON_FILE@/logo.icns/g" -e "s/@VERSION@/${version}/g" "$(dirname "$0")/macos/Info.plist.in" > "$app/Contents/Info.plist"
    cp "$(dirname "$0")/../docs/images/brand/logo.icns" "$app/Contents/Resources/logo.icns"
    chmod +x "$app/Contents/MacOS/$exe"
    # The music editor's plugin helper (MainframeEngine.Editor/runtimes/<rid>/native; absent before natives.yml built it):
    # a helper executable, so in Contents/MacOS, where PluginHostClient.Locate looks beside the editor's executable.
    helper=()
    if [[ -f "$app/Contents/MonoBundle/mfplughost" ]]; then
      mv "$app/Contents/MonoBundle/mfplughost" "$app/Contents/MacOS/mfplughost"
      chmod +x "$app/Contents/MacOS/mfplughost"
      # Under a Developer ID (hardened runtime) it gets its own entitlements: it loads vendors' VST3 bundles.
      helper=(--helper "MacOS/mfplughost=$(cd "$(dirname "$0")/macos" && pwd)/mfplughost.entitlements")
    else
      echo "note: no mfplughost for $rid: the packaged editor renders songs to .wav" >&2
    fi
    if [[ "$(file -b "$app/Contents/MacOS/$exe")" != *Mach-O* ]]; then
      # CI's packaging smoke runs on a stand-in publish folder (a text file for the executable): nothing to sign.
      echo "note: $exe is not a Mach-O apphost (a stand-in?): the bundle is not signed" >&2
    elif ! LC_ALL=C grep -aqF "../MonoBundle/$exe.dll" "$app/Contents/MacOS/$exe"; then
      echo "error: $publish_dir/$exe does not start ../MonoBundle/$exe.dll: publish the editor for $rid with" >&2
      echo "       MacBundleAppDirectory=MonoBundle (MainframeEngine.Editor.csproj sets it)" >&2
      exit 1
    elif command -v codesign >/dev/null; then
      # Inside out (the dylibs and the helper, then the bundle), ad hoc unless MF_SIGN_IDENTITY names a Developer ID;
      # then codesign --verify --deep --strict.
      bash "$(dirname "$0")/macos/sign-app.sh" "$app" --identity "${MF_SIGN_IDENTITY:--}" ${helper[@]+"${helper[@]}"}
    else
      echo "warning: no codesign (not macOS): $app_name is not signed" >&2
    fi
    tar -C "$staging" -czf "$out_dir/${name}.tar.gz" "$app_name"
    ;;
  win-*)
    mkdir -p "$staging/$name" && cp -R "$publish_dir"/. "$staging/$name/"
    archive="$out_dir/${name}.zip"
    if command -v zip >/dev/null; then
      (cd "$staging" && zip -qr "$archive" "$name")
    else
      # Windows runners have no zip: use PowerShell, which needs Windows paths (Git Bash's /d/a/... are not).
      src="$staging/$name"
      if command -v cygpath >/dev/null; then src="$(cygpath -w "$src")"; archive="$(cygpath -w "$archive")"; fi
      powershell -NoProfile -NonInteractive -Command "\$ErrorActionPreference = 'Stop'; Compress-Archive -Path '$src' -DestinationPath '$archive' -Force"
    fi
    ;;
  linux-*)
    mkdir -p "$staging/$name" && cp -R "$publish_dir"/. "$staging/$name/"
    chmod +x "$staging/$name/MainframeEngine.Editor"
    if [[ -f "$staging/$name/mfplughost" ]]; then chmod +x "$staging/$name/mfplughost"; fi
    # Launcher entry: ./install.sh writes the .desktop file (absolute paths, so it runs where the folder was extracted).
    cp "$(dirname "$0")/linux/install-editor.sh" "$staging/$name/install.sh"
    cp "$(dirname "$0")/linux/com.mainframegames.editor.desktop.in" "$staging/$name/"
    chmod +x "$staging/$name/install.sh"
    tar -C "$staging" -czf "$out_dir/${name}.tar.gz" "$name"
    ;;
  *) echo "unsupported RID: $rid" >&2; exit 1 ;;
esac
echo "packaged $out_dir/${name}"
