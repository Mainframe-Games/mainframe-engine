#!/usr/bin/env bash
# Collects the managed symbols of a publish folder (G15.8, docs/design/crash-reporting.md#symbols): every portable PDB
# with the assembly it belongs to (mf-symbolicate matches a crash report's frames to an assembly by its MVID, then reads
# that assembly's PDB), keeping the folder layout.
# Usage: build/collect-symbols.sh <publish-dir> <out-dir> [--move]
#   --move   also deletes the PDBs from <publish-dir> (a game exported with "symbols": "separate")
# publish.yml zips the editor's into MainframeEngine-<version>-symbols.zip; package-game.sh --symbols separate uses --move.
set -euo pipefail
src="$(cd "$1" && pwd)"; mkdir -p "$2"; out="$(cd "$2" && pwd)"; move="${3:-}"
count=0
while IFS= read -r -d '' pdb; do
  base="${pdb%.pdb}"
  for ext in dll exe; do
    [ -f "$base.$ext" ] || continue
    rel="${pdb#"$src"/}"
    mkdir -p "$out/$(dirname "$rel")"
    cp "$pdb" "$out/$rel"
    cp "$base.$ext" "$out/${rel%.pdb}.$ext"
    count=$((count + 1))
    [ "$move" = "--move" ] && rm -f "$pdb"
    break
  done
done < <(find "$src" -name '*.pdb' -type f -print0)
echo "Collected $count symbol file(s) from $src into $out"
