#!/usr/bin/env bash
# Packs the NuGet packages (docs/design/distribution-nuget.md) at one version:
#   MainframeEngine            the engine. mf-l10n, mf-export and mf-shaders are built first: the package carries them in
#                              tools/, and they reference the engine, so its pack cannot build them.
#   MainframeEngine.Templates  dotnet new mfgame, whose default engine version is this version.
# Used by `just pack`, build/template-smoke.sh (ENGINE_SOURCE=package) and .github/workflows/publish.yml.
#
# usage: build/pack-nuget.sh [version] [out-dir]     (defaults: 0.0.0-dev, artifacts/nuget)
# env:   PACK_ARGS (extra dotnet build/pack arguments, e.g. -p:CompileShaders=false -warnaserror)
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.0.0-dev}"
out="${2:-$repo/artifacts/nuget}"

# shellcheck disable=SC2086 # PACK_ARGS is a list of arguments
dotnet build "$repo/Tools/MainframeEngine.L10n/MainframeEngine.L10n.csproj" -c Release -p:Version="$version" ${PACK_ARGS:-}
# shellcheck disable=SC2086
dotnet build "$repo/Tools/MainframeEngine.Export.Cli/MainframeEngine.Export.Cli.csproj" -c Release -p:Version="$version" ${PACK_ARGS:-}
# shellcheck disable=SC2086
dotnet build "$repo/Tools/MainframeEngine.ShaderBuild/MainframeEngine.ShaderBuild.csproj" -c Release -p:Version="$version" ${PACK_ARGS:-}
# shellcheck disable=SC2086
dotnet pack "$repo/MainframeEngine/MainframeEngine.csproj" -c Release -p:Version="$version" -o "$out" ${PACK_ARGS:-}
# shellcheck disable=SC2086
dotnet pack "$repo/Templates/MainframeEngine.Templates/MainframeEngine.Templates.csproj" -c Release -p:Version="$version" -o "$out" ${PACK_ARGS:-}

# A file whose Git LFS objects the checkout lacks packs as its pointer (publish.yml's package job pulls only some paths;
# #168 shipped the template's icon.png that way). Fail on any packed pointer instead of releasing it.
lfs_header='version https://git-lfs.github.com/spec/v1'
unpacked="$(mktemp -d)"
trap 'rm -rf "$unpacked"' EXIT
pointers=()
for pkg in "MainframeEngine.$version.nupkg" "MainframeEngine.Templates.$version.nupkg"; do
    unzip -q "$out/$pkg" -d "$unpacked/$pkg"
    while IFS= read -r -d '' file; do
        # Pointers are at most 1024 bytes (the LFS spec); tr drops NULs so binary files do not warn.
        if [[ "$(head -c ${#lfs_header} "$file" | tr -d '\0')" == "$lfs_header" ]]; then
            pointers+=("$pkg: ${file#"$unpacked/$pkg/"}")
        fi
    done < <(find "$unpacked/$pkg" -type f -size -1025c -print0)
done
if ((${#pointers[@]})); then
    echo "pack-nuget: Git LFS pointers packed instead of their files (git lfs pull those paths):" >&2
    printf '  %s\n' "${pointers[@]}" >&2
    exit 1
fi

echo "pack-nuget: $out/MainframeEngine.$version.nupkg, $out/MainframeEngine.Templates.$version.nupkg"
