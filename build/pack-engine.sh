#!/usr/bin/env bash
# Packs the MainframeEngine NuGet package (docs/design/distribution-nuget.md): builds mf-l10n first (the package carries
# it in tools/mf-l10n, and it references the engine, so the engine's pack cannot build it), then dotnet pack.
# Used by `just pack`, build/template-smoke.sh (ENGINE_SOURCE=package) and .github/workflows/publish.yml.
#
# usage: build/pack-engine.sh [version] [out-dir]     (defaults: 0.0.0-dev, artifacts/nuget)
# env:   PACK_ARGS (extra dotnet build/pack arguments, e.g. -p:CompileShaders=false -warnaserror)
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.0.0-dev}"
out="${2:-$repo/artifacts/nuget}"

# shellcheck disable=SC2086 # PACK_ARGS is a list of arguments
dotnet build "$repo/Tools/MainframeEngine.L10n/MainframeEngine.L10n.csproj" -c Release -p:Version="$version" ${PACK_ARGS:-}
# shellcheck disable=SC2086
dotnet pack "$repo/MainframeEngine/MainframeEngine.csproj" -c Release -p:Version="$version" -o "$out" ${PACK_ARGS:-}
echo "pack-engine: $out/MainframeEngine.$version.nupkg"
