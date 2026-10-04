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

# Unit tests (no GPU)
test:
    dotnet test Tests/MainframeEngine.Tests

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

# Run the Sandbox (extra args are passed through, e.g. just sandbox --qa-capture out)
sandbox *args:
    dotnet run --project MainframeEngine.Sandbox -- {{args}}

# Screenshot the Sandbox at fixed frames into artifacts/qa, then exit
qa frames="30,90,180":
    dotnet run --project MainframeEngine.Sandbox -c Release -- --qa-capture {{artifacts / "qa"}} --qa-frames {{frames}}

# Run benchmarks and compare with baseline.json (fails on >10% slower or more allocation)
bench filter="*":
    dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '{{filter}}' --artifacts {{artifacts / "bench"}} --baseline-compare Tests/MainframeEngine.Benchmarks/baseline.json

# Re-record baseline.json on this machine (all benchmarks)
bench-baseline:
    dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '*' --artifacts {{artifacts / "bench"}} --baseline-write Tests/MainframeEngine.Benchmarks/baseline.json

# Apply dotnet format (the vendored Spine runtime is excluded)
format:
    dotnet format {{solution}} --exclude Plugins/Spine

# Verify formatting without changing files (what CI runs)
format-check:
    dotnet format {{solution}} --verify-no-changes --exclude Plugins/Spine

# Remove build outputs and artifacts
clean:
    dotnet clean {{solution}} -v q
    {{ if os_family() == "windows" { "if (Test-Path artifacts) { Remove-Item -Recurse -Force artifacts }" } else { "rm -rf artifacts" } }}
