---
summary: >-
  Release packing pulls the template's LFS objects (1.1.6 shipped mfgame's `Assets/icon.png` as an LFS pointer, so new
  games crashed at start-up, #168), and `build/pack-nuget.sh` fails on any LFS pointer packed in place of its file.
---

# 2026-11-04: Template icon shipped as an LFS pointer

Branch `fix/template-lfs-icon`. No plan (small). Issue #168.

## What changed

- `.github/workflows/publish.yml`, `package` job: `git lfs pull --include` adds `Templates/**`. Without it the checkout
  (`lfs: false`) kept the template's `content/mfgame/Assets/icon.png` as its pointer, `dotnet pack` packed that, and
  `Engine.SetWindowIcon` threw in StbImageSharp on the first run of every new game.
- `build/pack-nuget.sh` unzips both packages after packing and exits 1 listing every file under 1025 bytes (the LFS
  pointer limit) that starts with `version https://git-lfs.github.com/spec/v1`. Covers `just pack`, the package mode
  of `build/template-smoke.sh` and the release.
- `docs/design/distribution-nuget.md` (Packing): the guard as step 4; step 1 names all three tools it builds.

## Decisions

- Check the packed files, not `git lfs ls-files`: the package is what ships, and a pointer from any source (a copied
  file, a future content path) is caught the same way.

## Dead ends

- None.

## Verified

- `build/pack-nuget.sh 0.0.0-lfscheck` with the real icon: exit 0.
- Same with the icon replaced by its pointer (`git lfs pointer --file=…`): exit 1, naming
  `MainframeEngine.Templates.0.0.0-lfscheck.nupkg: content/mfgame/Assets/icon.png`.
- Not run: the release workflow itself (manual, `main` only). The template `.gitignore`'s `.mainframe/` fix the issue
  mentions is already on `main` (18479d03) and ships with the next release.
