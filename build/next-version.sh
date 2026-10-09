#!/usr/bin/env bash
# Prints the next release version (SemVer) from the latest vX.Y.Z tag: build/next-version.sh [patch|minor|major]
# (default patch; minor → X.(Y+1).0, major → (X+1).0.0). No release tag yet → 1.0.0 (the first release).
# Pre-release/other tags are ignored. Requires tags fetched.
set -euo pipefail

bump="${1:-patch}"
case "$bump" in
  patch | minor | major) ;;
  *) echo "next-version.sh: expected patch, minor or major, got '$bump'" >&2; exit 2 ;;
esac

latest="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1 || true)"
if [[ -z "$latest" ]]; then
  echo "1.0.0"
  exit 0
fi
IFS=. read -r major minor patch <<<"$latest"
case "$bump" in
  patch) echo "${major}.${minor}.$((patch + 1))" ;;
  minor) echo "${major}.$((minor + 1)).0" ;;
  major) echo "$((major + 1)).0.0" ;;
esac
