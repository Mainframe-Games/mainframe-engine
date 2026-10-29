#!/usr/bin/env bash
# Packs the NuGet packages (docs/design/distribution-nuget.md) at one version:
#   MainframeEngine            the engine. mf-l10n and mf-export are built first: the package carries them in
#                              tools/mf-l10n and tools/mf-export, and they reference the engine, so its pack cannot build them.
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
dotnet pack "$repo/MainframeEngine/MainframeEngine.csproj" -c Release -p:Version="$version" -o "$out" ${PACK_ARGS:-}
# shellcheck disable=SC2086
dotnet pack "$repo/Templates/MainframeEngine.Templates/MainframeEngine.Templates.csproj" -c Release -p:Version="$version" -o "$out" ${PACK_ARGS:-}
echo "pack-nuget: $out/MainframeEngine.$version.nupkg, $out/MainframeEngine.Templates.$version.nupkg"
