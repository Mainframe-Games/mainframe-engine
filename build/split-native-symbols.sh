#!/usr/bin/env bash
# Splits the debug info out of the staged native libraries (G15.8, natives.yml): the binaries committed under
# runtimes/<rid>/native stay stripped, and their symbols become a workflow artefact (native-symbols-<leg>) that a release
# attaches to its symbols zip, so native frames in our shims resolve to functions.
#   macOS   dsymutil <lib> -o <out>/<lib>.dSYM, then strip -x (local symbols; exports stay)
#   Linux   objcopy --only-keep-debug <lib> <out>/<lib>.debug, then --strip-debug + --add-gnu-debuglink
#   Windows the linker's .pdb next to each .dll/.exe in the build tree is copied to <out>
# Usage: build/split-native-symbols.sh <staging-dir> <build-dir> <out-dir>
set -euo pipefail
staging="$1"; build="$2"; mkdir -p "$3"; out="$(cd "$3" && pwd)"
os="$(uname -s)"
while IFS= read -r -d '' lib; do
  name="$(basename "$lib")"
  rid="$(basename "$(dirname "$(dirname "$lib")")")"
  dest="$out/$rid"; mkdir -p "$dest"
  case "$os" in
    Darwin)
      dsymutil "$lib" -o "$dest/$name.dSYM"
      strip -x "$lib"
      ;;
    Linux)
      objcopy --only-keep-debug "$lib" "$dest/$name.debug"
      objcopy --strip-debug --add-gnu-debuglink="$dest/$name.debug" "$lib"
      ;;
    MINGW*|MSYS*|CYGWIN*)
      pdb="$(find "$build" -name "${name%.*}.pdb" -type f -not -path '*/CMakeFiles/*' | head -n 1)"
      if [ -n "$pdb" ]; then cp "$pdb" "$dest/"; else echo "::warning::no .pdb for $name"; fi
      ;;
  esac
done < <(find "$staging" -path '*/native/*' -type f -print0)
find "$out" -maxdepth 3 -print | sed "s|^$out|symbols|"
