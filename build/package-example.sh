#!/usr/bin/env bash
# Zips an example project (Examples/<Demo|Forest>) for a release: MainframeEngine.<Example>-v<version>.zip in <out-dir>,
# top folder MainframeEngine.<Example>/ (the editor's Download menu fetches it: its own version's, or the latest release's).
# Usage: build/package-example.sh <Demo|Forest> <version> <out-dir>. Needs the example's LFS content pulled (fails on pointer files).
set -euo pipefail
example="$1"; version="$2"; out="$(mkdir -p "$3" && cd "$3" && pwd)"
case "$example" in Demo|Forest) ;; *) echo "usage: $0 <Demo|Forest> <version> <out-dir>" >&2; exit 2 ;; esac
repo="$(cd "$(dirname "$0")/.." && pwd)"
name="MainframeEngine.$example"
stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
dest="$stage/$name"
mkdir -p "$dest"
(cd "$repo/Examples/$example" && git ls-files -z --cached --others --exclude-standard) |
  while IFS= read -r -d '' file; do
    case "$file" in bin/*|obj/*|*/bin/*|*/obj/*|*.user|.vs/*) continue ;; esac
    mkdir -p "$dest/$(dirname "$file")"
    cp "$repo/Examples/$example/$file" "$dest/$file"
  done
if grep -rl --binary-files=text '^version https://git-lfs.github.com/spec/v1' "$dest" >/dev/null; then
  echo "error: Git LFS pointer files in the $example (run git lfs pull --include='Examples/$example/**'):" >&2
  grep -rl --binary-files=text '^version https://git-lfs.github.com/spec/v1' "$dest" >&2
  exit 1
fi

# The example builds outside this repo, where the root Directory.Packages.props (central package management) does not apply:
# ship one with exactly the packages its projects reference, versions read from the root file now.
central="$repo/Directory.Packages.props"
packages="$(cd "$dest" && find . -name '*.csproj' -print0 | xargs -0 grep -h -o '<PackageReference [^>]*Include="[^"]*"' |
  sed -E 's/.*Include="([^"]*)".*/\1/' | sort -u)"
{
  echo '<Project>'
  echo '  <!-- Central package versions for this project, copied from the engine checkout it was packaged from. -->'
  echo '  <PropertyGroup>'
  echo '    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>'
  echo '  </PropertyGroup>'
  echo '  <ItemGroup>'
  for package in $packages; do
    entry="$(grep -E "<PackageVersion Include=\"$package\" Version=\"[^\"]*\"" "$central" | head -n 1 || true)"
    pin="$(printf '%s' "$entry" | sed -E 's/.*Version="([^"]*)".*/\1/')"
    if [ -z "$entry" ] || [ -z "$pin" ] || [[ "$pin" == *'$('* ]]; then
      echo "error: the $example references $package, which has no literal version in Directory.Packages.props" >&2
      exit 1
    fi
    echo "    <PackageVersion Include=\"$package\" Version=\"$pin\" />"
  done
  echo '  </ItemGroup>'
  echo '</Project>'
} > "$dest/Directory.Packages.props"
(cd "$stage" && zip -qr -X "$out/$name-v$version.zip" "$name")
ls -la "$out/$name-v$version.zip"
