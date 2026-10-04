# Mainframe Engine — local commands. `just` lists them. CI calls dotnet directly (see .github/workflows/ci.yml).
# Shader recipes need a POSIX sh (macOS/Linux, or Git Bash on Windows) plus glslc/spirv-val from the Vulkan SDK.

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

solution := "MainframeEngine.sln"
artifacts := justfile_directory() / "artifacts"

# List recipes
default:
    @just --list

# Build the solution (0 warnings enforced: warnings are errors)
build config="Debug":
    dotnet build {{solution}} -c {{config}}

# Unit tests (no GPU): engine and editor
test:
    dotnet test Tests/MainframeEngine.Tests
    dotnet test Tests/MainframeEngine.Editor.Tests

# Unit tests with coverage (Cobertura XML under artifacts/coverage)
coverage:
    dotnet test Tests/MainframeEngine.Tests --collect:"XPlat Code Coverage" --results-directory {{artifacts / "coverage"}}

# Render tests: golden images, validation gate, allocation gate (needs a Vulkan device + display)
test-render:
    dotnet test Tests/MainframeEngine.RenderTests

# Unit + render tests
test-all: test test-render

# Re-record the golden images for this machine's driver (moltenvk, lavapipe, ...); review before committing
golden-update $UPDATE_GOLDENS="1":
    dotnet test Tests/MainframeEngine.RenderTests

# Compile every shader to SPIR-V (Vulkan 1.2), spirv-val it, and refresh shaders.lock
shaders:
    sh build/shaders.sh compile
    sh build/shaders.sh lock

# Fail if a shader source or .spv no longer matches shaders.lock
shaders-check:
    sh build/shaders.sh check

# Run the editor (extra args are passed through, e.g. just editor MainframeEngine.Sandbox/Content/Scenes/Sandbox.mscene)
editor *args:
    dotnet run --project MainframeEngine.Editor -- {{args}}

# Scripted editor QA: input + captures into artifacts/qa-editor (default script: the Sandbox walkthrough)
qa-editor script="Tests/QA/editor-walkthrough.qa":
    dotnet run --project MainframeEngine.Editor -c Release -- MainframeEngine.Sandbox/Content/Scenes/Sandbox.mscene --hidden --qa-script {{script}} --qa-out {{artifacts / "qa-editor"}}

# Run the Sandbox (extra args are passed through, e.g. just sandbox --qa-capture out)
sandbox *args:
    dotnet run --project MainframeEngine.Sandbox -- {{args}}

# Screenshot the Sandbox at fixed frames into artifacts/qa, then exit
qa frames="30,90,180":
    dotnet run --project MainframeEngine.Sandbox -c Release -- --qa-capture {{artifacts / "qa"}} --qa-frames {{frames}}

# Create a game from the mfgame template against this checkout, build it and run it for N frames (CI job "template")
template-smoke frames="30":
    build/template-smoke.sh "{{artifacts / "template-smoke"}}" {{frames}}

# Pack the dotnet new templates (MainframeEngine.Templates) into artifacts/templates
template-pack:
    dotnet pack Templates/MainframeEngine.Templates -c Release -o {{artifacts / "templates"}}

# Run benchmarks and compare with baseline.json (fails on >10% slower or more allocation)
bench filter="*":
    dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '{{filter}}' --artifacts {{artifacts / "bench"}} --baseline-compare Tests/MainframeEngine.Benchmarks/baseline.json

# Re-record baseline.json on this machine (all benchmarks)
bench-baseline:
    dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '*' --artifacts {{artifacts / "bench"}} --baseline-write Tests/MainframeEngine.Benchmarks/baseline.json

# Publish + package the editor exactly like the release workflow (default RID: this machine's)
publish-local rid="" version="0.0.0-local":
    #!/usr/bin/env bash
    set -euo pipefail
    rid="{{rid}}"
    if [[ -z "$rid" ]]; then rid="$(dotnet --info | awk '/ RID:/{print $2; exit}')"; fi
    dotnet publish MainframeEngine.Editor/MainframeEngine.Editor.csproj -c Release -r "$rid" --self-contained \
      -p:Version={{version}} -p:PublishReadyToRun=true -o "{{artifacts}}/publish/$rid"
    build/package-editor.sh "$rid" "{{version}}" "{{artifacts}}/publish/$rid" "{{artifacts}}/release"

# Next release version the publish workflow would create (patch bump of the latest vX.Y.Z tag)
next-version:
    git fetch -q --tags origin && build/next-version.sh

# Regenerate the logo PNGs, .ico and .icns from docs/images/brand/*.svg (needs inkscape; .icns needs macOS iconutil)
brand:
    #!/usr/bin/env bash
    set -euo pipefail
    cd docs/images/brand
    mkdir -p png
    # Full-bleed tile (Windows .ico, Linux, docs): the simplified artwork at 48 px and below.
    for n in 16 24 32 48; do inkscape logo-small.svg -w $n -h $n -o png/logo-$n.png; done
    for n in 64 128 256 512 1024; do inkscape logo.svg -w $n -h $n -o png/logo-$n.png; done
    python3 ../../../build/brand/make-ico.py logo.ico png/logo-{16,24,32,48,64,128,256}.png
    # macOS: Apple's icon grid (824 px tile on a 1024 canvas) for the .icns and the Dock icon at runtime.
    for n in 16 32 64 128 256 512 1024; do inkscape logo-macos.svg -w $n -h $n -o png/logo-macos-$n.png; done
    if command -v iconutil >/dev/null; then
      set_dir="$(mktemp -d)/Mainframe.iconset"; mkdir -p "$set_dir"
      for n in 16 32 128 256 512; do
        cp png/logo-macos-$n.png "$set_dir/icon_${n}x${n}.png"
        cp png/logo-macos-$((n * 2)).png "$set_dir/icon_${n}x${n}@2x.png"
      done
      iconutil -c icns "$set_dir" -o logo.icns
    else
      echo "iconutil not found (macOS only): logo.icns left unchanged"
    fi
    cp png/logo-{32,48,256,512}.png png/logo-macos-512.png ../../../MainframeEngine/Content/Brand/

# Apply dotnet format (the vendored Spine runtime is excluded)
format:
    dotnet format {{solution}} --exclude Plugins/Spine

# Verify formatting without changing files (what CI runs)
format-check:
    dotnet format {{solution}} --verify-no-changes --exclude Plugins/Spine

# --- Localization (docs/design/localization.md) -------------------------------------------------------------------
# The Sandbox's catalogs: Content/locale/messages.pot and <locale>/LC_MESSAGES/messages.po|.mo.
l10n_dir := "MainframeEngine.Sandbox/Content/locale"
l10n := "dotnet run --project Tools/MainframeEngine.L10n -c Release --"

# Extract strings (C# via GetText.Extractor; RML — the Sandbox HUD and the engine widget library — and [Export(Translatable)] scene values via mf-l10n) into messages.pot, merge it into every .po and regenerate the qps pseudo-locale
l10n-extract:
    dotnet tool restore
    dotnet build MainframeEngine.Sandbox -p:CompileLocales=false
    dotnet tool run GetText.Extractor -s MainframeEngine.Sandbox/Src -t {{artifacts / "l10n" / "code.pot"}} -u -o -as _ -ad P -ap N -adp NP
    {{l10n}} extract -o {{l10n_dir}}/messages.pot --root . --project "Mainframe Engine Sandbox" --include {{artifacts / "l10n" / "code.pot"}} --rml MainframeEngine.Sandbox/Content --rml MainframeEngine/Content/UI --scenes MainframeEngine.Sandbox/Content --assembly MainframeEngine.Sandbox/bin/Debug/net10.0/MainframeEngine.Sandbox.dll
    {{l10n}} update --pot {{l10n_dir}}/messages.pot --dir {{l10n_dir}}
    {{l10n}} pseudo --pot {{l10n_dir}}/messages.pot --dir {{l10n_dir}} --charset latin1

# Compile every .po to the committed .mo next to it (the build compiles them into the output itself)
l10n-compile:
    {{l10n}} compile --dir {{l10n_dir}}

# Fail if a committed .mo is stale or a translation breaks its placeholders (and compare with GNU msgfmt when installed)
l10n-check:
    {{l10n}} check --dir {{l10n_dir}} --msgfmt

# Translation coverage per locale
l10n-stats:
    {{l10n}} stats --dir {{l10n_dir}} --pot {{l10n_dir}}/messages.pot

# Remove build outputs and artifacts
clean:
    dotnet clean {{solution}} -v q
    {{ if os_family() == "windows" { "if (Test-Path artifacts) { Remove-Item -Recurse -Force artifacts }" } else { "rm -rf artifacts" } }}
