#!/usr/bin/env bash
# Zips Examples/Demo for a release: MainframeEngine.Demo-v<version>.zip and MainframeEngine.Demo.zip in <out-dir>.
# Usage: build/package-demo.sh <version> <out-dir>. Needs the Demo's LFS content pulled (fails on pointer files).
set -euo pipefail
version="$1"; out="$(mkdir -p "$2" && cd "$2" && pwd)"
repo="$(cd "$(dirname "$0")/.." && pwd)"
stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
dest="$stage/MainframeEngine.Demo"
mkdir -p "$dest"
(cd "$repo/Examples/Demo" && git ls-files -z --cached --others --exclude-standard) |
  while IFS= read -r -d '' file; do
    case "$file" in bin/*|obj/*|*/bin/*|*/obj/*|*.user|.vs/*) continue ;; esac
    mkdir -p "$dest/$(dirname "$file")"
    cp "$repo/Examples/Demo/$file" "$dest/$file"
  done
if grep -rl --binary-files=text '^version https://git-lfs.github.com/spec/v1' "$dest" >/dev/null; then
  echo "error: Git LFS pointer files in the Demo (run git lfs pull --include='Examples/Demo/**'):" >&2
  grep -rl --binary-files=text '^version https://git-lfs.github.com/spec/v1' "$dest" >&2
  exit 1
fi
(cd "$stage" && zip -qr -X "$out/MainframeEngine.Demo-v$version.zip" MainframeEngine.Demo)
cp "$out/MainframeEngine.Demo-v$version.zip" "$out/MainframeEngine.Demo.zip"
ls -la "$out"/MainframeEngine.Demo*.zip
