#!/usr/bin/env bash
# Packages a published editor folder into the release archive for one RID.
# Usage: build/package-editor.sh <rid> <version> <publish-dir> <out-dir>
# Used by .github/workflows/publish.yml and `just publish-local`.
set -euo pipefail

rid="$1" version="$2" publish_dir="$3" out_dir="$4"
name="MainframeEngine-${version}-${rid}"
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

case "$rid" in
  osx-*)
    # The bundle folder and CFBundleName/CFBundleDisplayName are the app name macOS shows (Dock, menu bar,
    # Finder); the executable keeps its assembly name. Dev builds get the same bundle (build/macos/DevAppBundle.targets).
    app_name="Mainframe Engine.app"
    app="$staging/$app_name"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$publish_dir"/. "$app/Contents/MacOS/"
    sed -e "s/@VERSION@/${version}/g" -e "s/@BUNDLE_ID_SUFFIX@//g" "$(dirname "$0")/macos/Info.plist.in" > "$app/Contents/Info.plist"
    cp "$(dirname "$0")/../docs/images/brand/logo.icns" "$app/Contents/Resources/logo.icns"
    chmod +x "$app/Contents/MacOS/MainframeEngine.Editor"
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
    tar -C "$staging" -czf "$out_dir/${name}.tar.gz" "$name"
    ;;
  *) echo "unsupported RID: $rid" >&2; exit 1 ;;
esac
echo "packaged $out_dir/${name}"
