#!/usr/bin/env bash
# Does a change touch code? Reads changed paths (one per line, repo-relative) on stdin and prints code=true or
# code=false, the output CI's `changes` job hands to the build, test, render and template jobs (ci.yml,
# docs/design/build-and-platforms.md#ci).
#
# code=false only when every path is documentation: docs/** (except docs/images/brand/**, which the editor icon, the
# NuGet package icons and the release packaging read), docs-site/**, memory/** or a top-level *.md (README.md,
# CLAUDE.md). Anything else is code, and so is an empty list: when unsure, run everything.
#
# usage: git diff --name-only origin/main... | build/ci-changes.sh
set -euo pipefail

count=0
code=false
while IFS= read -r path; do
  [ -n "$path" ] || continue
  count=$((count + 1))
  case "$path" in
    docs/images/brand/*) code=true ;;
    docs/* | docs-site/* | memory/*) ;;
    */*) code=true ;;
    *.md) ;;
    *) code=true ;;
  esac
done

if [ "$count" -eq 0 ]; then
  code=true
fi
echo "code=$code"
