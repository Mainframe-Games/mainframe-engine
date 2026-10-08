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

[ -e "$root/Plugins/Spine/.git" ] || { echo "Plugins/Spine is not checked out: git submodule update --init Plugins/Spine" >&2; exit 1; }

# Files to send: everything git would commit (tracked + untracked-but-not-ignored, so new goldens count), the
# vendored Spine runtime's C# sources, and .git (SourceLink stamps the commit into the version) without LFS objects,
# linked worktrees or the native submodules' histories.
list="$(mktemp)"
stage="$(mktemp -d)"
trap 'rm -rf "$list" "$stage"' EXIT
(
  cd "$root"
  git ls-files -co --exclude-standard -z | perl -0 -ne 'chomp; print "$_\0" if -f $_ || -l $_'
  git -C Plugins/Spine ls-files -z -- spine-csharp | perl -0 -ne 'chomp; print "Plugins/Spine/$_\0"'
) > "$list"

# Writes the tar stream of .git and Plugins/Spine/.git. In a linked worktree (.git is a file: `gitdir:
# <common>/worktrees/<name>`, a host path) it sends a self-contained .git instead: the common dir's objects, refs
# and config by symlink with the worktree's HEAD and index, and the worktree's Spine module (which lives under
# <common>/worktrees/<name>/modules) moved to .git/modules with its .git file and core.worktree pointing there.
git_tar() {
  if [ -d "$root/.git" ]; then
    tar --no-xattrs --no-mac-metadata -C "$root" -cf - \
      --exclude .git/lfs --exclude .git/worktrees --exclude .git/modules/Native .git Plugins/Spine/.git
    return
  fi
  local gitdir common entry
  gitdir="$(git -C "$root" rev-parse --absolute-git-dir)"
  common="$(cd "$root" && cd "$(git rev-parse --git-common-dir)" && pwd)"
  mkdir -p "$stage/.git/modules/Plugins" "$stage/Plugins/Spine"
  for entry in "$common"/*; do
    case "${entry##*/}" in HEAD|index|lfs|worktrees|modules) ;; *) ln -s "$entry" "$stage/.git/" ;; esac
  done
  cp "$gitdir/HEAD" "$gitdir/index" "$stage/.git/"
  cp -R "$(git -C "$root/Plugins/Spine" rev-parse --absolute-git-dir)" "$stage/.git/modules/Plugins/Spine"
  git config --file "$stage/.git/modules/Plugins/Spine/config" core.worktree ../../../../Plugins/Spine
  printf 'gitdir: ../../.git/modules/Plugins/Spine\n' > "$stage/Plugins/Spine/.git"
  # -H archives what the command-line symlinks point to.
  (cd "$stage" && tar --no-xattrs --no-mac-metadata -H -cf - .git/* Plugins/Spine/.git)
}

echo "Copying the working tree into the container and running the '$suite' tests (x86_64 under emulation: slow)..."
(
  cd "$root"
  tar --no-xattrs --no-mac-metadata --null -cf - -n -T "$list"
  git_tar
) | docker run --rm -i --platform "$platform" \
      -v mainframe-linux-work:/work -v mainframe-linux-nuget:/cache/nuget -v "$out":/out \
      "$image" bash -c 'rm -rf /src && mkdir -p /src && tar -xif - -C /src && exec bash /src/build/linux/inside.sh "$@"' \
      bash "$suite" "$@"
