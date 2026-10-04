#!/usr/bin/env bash
# Packages a published editor folder into the release archive for one RID.
# Usage: build/package-editor.sh <rid> <version> <publish-dir> <out-dir>
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
    app="$staging/Mainframe Editor.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$publish_dir"/. "$app/Contents/MacOS/"
    sed "s/@VERSION@/${version}/g" "$(dirname "$0")/macos/Info.plist.in" > "$app/Contents/Info.plist"
    chmod +x "$app/Contents/MacOS/MainframeEngine.Editor"
    tar -C "$staging" -czf "$out_dir/${name}.tar.gz" "Mainframe Editor.app"
    ;;
  win-*)
    mkdir -p "$staging/$name" && cp -R "$publish_dir"/. "$staging/$name/"
    (cd "$staging" && if command -v zip >/dev/null; then zip -qr "$out_dir/${name}.zip" "$name"; \
      else powershell -NoProfile -Command "Compress-Archive -Path '$name' -DestinationPath '$out_dir/${name}.zip'"; fi)
    ;;
  linux-*)
    mkdir -p "$staging/$name" && cp -R "$publish_dir"/. "$staging/$name/"
    chmod +x "$staging/$name/MainframeEngine.Editor"
    tar -C "$staging" -czf "$out_dir/${name}.tar.gz" "$name"
    ;;
  *) echo "unsupported RID: $rid" >&2; exit 1 ;;
esac
echo "packaged $out_dir/${name}"
