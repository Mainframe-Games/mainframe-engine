#!/usr/bin/env bash
# Prints the next release version (SemVer, patch bump) from the latest vX.Y.Z tag.
# No release tag yet → 0.1.0. Pre-release/other tags are ignored. Requires tags fetched.
set -euo pipefail

latest="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1 || true)"
if [[ -z "$latest" ]]; then
  echo "0.1.0"
  exit 0
fi
IFS=. read -r major minor patch <<<"$latest"
echo "${major}.${minor}.$((patch + 1))"
