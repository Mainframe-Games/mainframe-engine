---
summary: >-
  Linux-host tooling: mf-shaders no longer crashes slangc 2026.1 (the obfuscated compile gets no #line directives;
  errors come from a non-obfuscated compile with them), and build/linux/run.sh runs on Linux (GNU tar, no buildx).
---

# 2026-11-03: Linux shader and Docker tooling

Branch `fix/linux-shader-tooling`. Small fix; both gaps were found while fixing the editor viewport flicker
(`fix/editor-viewport-taa-flicker`, its log's "Tooling notes"). Written before #156 renamed the files: `.gdshader` and
`gdshader_folders` below are now `.mfshader` and `mfshader_folders` (the merge renamed the test's files too).

## What changed

- **`CanvasShaderBuild.Compile`** (`Rendering/Canvas/Shader.cs`). slangc 2026.1's Linux x86_64 release segfaults (exit 139)
  in `obfuscateModuleLocs` → `SourceMap::getSourceFileIndex` when `-obfuscate` meets the `#line` directives
  `SpatialShaderCompiler` writes. It depends on heap layout, so a retry never helps: 18 of the 19 gdshaders in
  `gdshader_folders` crashed on the owner's CachyOS box.
  - The obfuscated compile now always gets the source without `#line` (`WithoutLineDirectives`: each becomes an empty
    line). `-obfuscate` drops every location anyway.
  - When that compile fails, a second one without `-obfuscate` and with the directives (into a throwaway `.spv`) gives
    the error text, so errors still read `broken.gdshader(5): error 30015: …`. Its output is used only when it also
    fails with output; otherwise the first compile's text stays.
  - The signal retry moved into `RunSlangc`, shared by both compiles.
- **`build/linux/run.sh`**:
  - The tar flags are chosen per implementation. bsdtar keeps `--no-mac-metadata` and `-H`. GNU tar gets `-h` (its
    `-H` is `--format`). Both take `--no-recursion` in place of bsdtar's `-n`.
  - The image's build context is a temporary directory with `global.json` and the Dockerfile. The legacy builder
    (no buildx) rejects `-f` with a tar on stdin ("ambiguous Dockerfile source").
  - `artifacts/linux` is emptied inside the container (`inside.sh`), and its results are chowned to the host user
    (`HOST_OWNER`). On a Linux host the container's root owned them, so the next run's `rm -rf` failed. Docker Desktop
    hides this on macOS.
  - The "x86_64 under emulation: slow" note only prints on non-x86_64 hosts.
- Docs: `shaders.md` (Building), `testing.md` (Linux tests in Docker).

## Decisions

- Always strip `#line` from the obfuscated compile, rather than retrying without it after a signal death. The output
  never depends on whether slangc happened to crash, and there is no crash path left to hit.
- The committed gdshader SPIR-V stays as it is (2026.1-52 builds). Nothing needed rebuilding.

## Dead ends

- The existing signal retry: the crash depends on heap layout, so the same input crashes on every attempt.
- `GLIBC_TUNABLES=glibc.malloc.mmap_threshold=0` (or `-g0`) hides the crash. Used only to make a reference build.

## Verified

- All 19 gdshaders rebuilt from scratch (copies without locks) with slangc 2026.1 on PATH: 19 built, 0 failed; before,
  18 failed with exit 139. The 182 `.spv` and 19 `.spvlock` files are byte-identical to a build that kept `#line`
  (made with `GLIBC_TUNABLES=glibc.malloc.mmap_threshold=0`, which hides the crash).
- The committed gdshader SPIR-V is unchanged: it is up to date, and the 2026.1-52 builds stay. A Linux 2026.1 rebuild
  differs in 14 fragment files, in equivalent comparisons only, as the flicker log found.
- `CanvasShaderBuildTests`: the stripping, the diagnostic arguments, and a slangc test that builds a spatial shader and
  checks a broken one's error names `broken.gdshader(6)`. The slangc test is skipped without slangc, as on CI's
  build-test runners. With the strip made a no-op, it fails with exit code 139.
- On this Linux host (GNU tar 1.35, Docker 29.9.2 with no buildx), from a linked worktree:
  - `just test-linux`: 3196 + 1803 + 460 pass.
  - `just render-tests-linux`: 222 pass on `llvmpipe (LLVM 20.1.2, 256 bits)`. It also removed the root-owned results of
    an earlier run.
  - One frame has no `lavapipe` golden: `auto-exposure-histogram_frame0005`. Only `moltenvk` has had one since ADR 0177.
    This predates the change, and the frame was not recorded here.
- The bsdtar branch was checked here with `tar` → bsdtar 3.8 and a stub `docker` capturing the stream. Extracted with
  `tar -i` as in the container, `.git` resolves `HEAD` and the Spine module. Not run on a Mac.
- Gates: `just build`, the Release `-warnaserror` build, `just test` (3196 + 1805 + 460, slangc on PATH),
  `just format-check`, `just shaders-check`, `check.sh`: ok. `just test-render` was not run on the host. This GPU
  (NVIDIA) has no goldens, no runtime code changed, and the lavapipe run above covers it.

## Not verified

- `run.sh` on a Mac (the bsdtar branch was only checked with Linux bsdtar and a stub `docker`).

## Next

- `fix/editor-viewport-taa-flicker` can record its four `lavapipe` goldens here with `just render-tests-linux`.
- `auto-exposure-histogram_frame0005` needs a reviewed `lavapipe` golden.
