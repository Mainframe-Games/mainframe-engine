#!/usr/bin/env bash
# Run CI's Linux tests locally in Docker: an x86_64 ubuntu:24.04 container with lavapipe, the validation layers and
# Xvfb (build/linux/Dockerfile), like the ci.yml build-test (ubuntu) and render-tests jobs.
#   build/linux/run.sh [all|unit|render] [extra `dotnet test` args, e.g. --filter FullyQualifiedName~Editor]
# The working tree (tracked + untracked, not ignored; LFS content as checked out) is copied into a cached volume, built
# Release with -warnaserror and tested. Results land in artifacts/linux/: TestResults/*.trx and render-tests/ (frames
# without a lavapipe golden under render-tests/new-goldens/lavapipe/). See docs/design/testing.md#linux-tests-in-docker.
set -euo pipefail
export COPYFILE_DISABLE=1 # no AppleDouble ._ files in the tar streams (bsdtar on macOS)

suite="${1:-all}"
shift || true
case "$suite" in all|unit|render) ;; *) echo "usage: $0 [all|unit|render] [dotnet test args...]" >&2; exit 2 ;; esac

root="$(cd "$(dirname "$0")/../.." && pwd)"
image="mainframe-linux-tests"
out="$root/artifacts/linux"
platform="linux/amd64" # CI's lavapipe is x86_64; goldens are recorded there

command -v docker >/dev/null || { echo "docker not found" >&2; exit 1; }

# The image only needs global.json and the Dockerfile (a tiny context instead of the whole checkout).
tar --no-xattrs --no-mac-metadata -C "$root" -cf - global.json build/linux/Dockerfile |
  docker build --quiet --platform "$platform" -t "$image" -f build/linux/Dockerfile - >/dev/null

rm -rf "$out"
mkdir -p "$out"

# Files to send: everything git would commit (tracked + untracked-but-not-ignored, so new goldens count), the
# vendored Spine runtime's C# sources, and .git (SourceLink stamps the commit into the version) without LFS objects,
# linked worktrees or the native submodules' histories.
list="$(mktemp)"
trap 'rm -f "$list"' EXIT
(
  cd "$root"
  git ls-files -co --exclude-standard -z | perl -0 -ne 'chomp; print "$_\0" if -f $_ || -l $_'
  git -C Plugins/Spine ls-files -z -- spine-csharp | perl -0 -ne 'chomp; print "Plugins/Spine/$_\0"'
  printf 'Plugins/Spine/.git\0'
) > "$list"

echo "Copying the working tree into the container and running the '$suite' tests (x86_64 under emulation: slow)..."
(
  cd "$root"
  tar --no-xattrs --no-mac-metadata --null -cf - -n -T "$list"
  tar --no-xattrs --no-mac-metadata -cf - --exclude .git/lfs --exclude .git/worktrees --exclude .git/modules/Native .git
) | docker run --rm -i --platform "$platform" \
      -v mainframe-linux-work:/work -v mainframe-linux-nuget:/cache/nuget -v "$out":/out \
      "$image" bash -c 'rm -rf /src && mkdir -p /src && tar -xif - -C /src && exec bash /src/build/linux/inside.sh "$@"' \
      bash "$suite" "$@"
