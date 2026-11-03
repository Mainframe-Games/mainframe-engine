---
summary: >-
  The Docker Linux tests are safe with several checkouts: one run at a time on the shared build volume (a second run
  waits), and a content sync so a run can no longer keep another checkout's newer outputs (the stale `.spv` of PR #164).
---

# 2026-11-04: Docker Linux tests with several checkouts

Branch `fix/linux-docker-shared-volume`. No plan (small).

## What changed

- `build/linux/inside.sh` takes `flock` on `/work/.linux-tests.lock` before touching `/out` or `/work`. A second run
  prints the holder (checkout, container, start time, from `/work/.linux-tests.owner`) and waits.
- The sync into `/work` is `rsync -rlpgoD --checksum` (`-a` without `-t`): changed files are rewritten with the current
  time, so they are newer than any output built from another checkout's version; unchanged files keep their stamps.
- A volume last synced by the old script (marker `/work/.linux-tests.synced` missing; the old `--delete` removes it) has
  every source restamped once.
- `build/linux/run.sh`: `docker run --init` with `TINI_KILL_PROCESS_GROUP=1`, so Ctrl-C stops a waiting container;
  passes `HOST_CHECKOUT` for the owner note.
- `docs/design/testing.md` (Linux tests in Docker) describes both, and that the container tests the committed `.spv`.

## Decisions

- One shared volume plus a lock, not a volume per checkout: worktrees are many and short-lived, so per-checkout volumes
  would start cold (a full Release build each) and leave a few GB behind per worktree. Runs on one machine compete for
  the CPU anyway (lavapipe), so serialising them costs little.
- Wait rather than fail: sessions running tests in the background would otherwise have to retry.
- Content sync rather than cleaning the shader outputs: the timestamp problem is not specific to shaders (C# compiles
  and every `PreserveNewest` content copy compare times the same way).

## Dead ends

- None.

## Verified

- Control with the old scripts: a second worktree built the volume, then this worktree's `DebugLines.vk.frag.spv`
  (edited, dated 2020) was synced; all 13 Release copies in `bin/` kept the old content.
- New scripts, same state: the marker triggered the restamp, and all 13 copies matched the edit. Then, new scripts only:
  the other worktree's run put the original back, this worktree's run put the edit back (2.5 s build, 15 s run).
- A second run started during the first printed the holder and waited; SIGINT to its `docker run` removed its container.
- Gates: see the PR.

## Not verified

- macOS Docker Desktop (flock on a volume inside its VM, `--init`); the owner's Mac.
- Two runs from old and new scripts at the same moment (the old one takes no lock).

## Next

- None.
