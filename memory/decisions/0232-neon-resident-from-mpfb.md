# ADR 0232 — Neon: the resident is an MPFB character, not a scripted body

- **Date:** 2026-11-01
- **Status:** accepted
- **Docs:** docs/design/neon.md, Examples/Neon/NOTICE.md

## Context

The first resident was a procedural figure: forms lofted along a skeleton and voxel remeshed. It looked bad, and a script
alone cannot produce a convincing person. The request was AAA quality. Real AAA characters come from scan data and
artists, which this project does not have. The options were a paid character (a licence that may not allow a public
repository) or MPFB, the MakeHuman Plugin for Blender, with its free asset packs.

## Decision

- **Build her from MPFB in headless Blender, seeded and repeatable.** `build_resident.py` still produces the same outputs,
  conventions, clip and bone name, so `NeonResident` and its tests barely change. `fetch_mpfb_packs.py` installs MPFB and
  its packs; neither ships.
- **Only assets whose licence is clear on both sides.** Pack pages can disagree with a file's own header:
  `marco_105_stocking01` is listed CC0 but its header says AGPL3 under another author, and this repository is MIT. Each
  asset she uses is checked in its pack metadata and in its own file, and listed in `NOTICE.md`.
- **Make the lingerie and stockings' texture ourselves.** The packs' underwear rendered badly. A shell of her own torso,
  with the cut and lace baked in her UV space, fits exactly and shares her weights. It stays full coverage.
- **Apply the pose as the rest pose,** so the mesh's bounds are her real ones and the idle stays small.

## Consequences

- She is switched off for now (`NeonApartment.ShowResident` is false, the bed is empty) until she is revisited; her art and
  code stay in the repository and her test turns her on.
- `just neon-art` needs MPFB and about 700 MB of packs in the user's Blender (installed on first run).
- Her quality is good indie, not AAA: no skin normal map or subsurface scattering, no strand hair, one rigid hair mesh.
- MakeHuman's licence page was unreachable when this was written (certificate error). The base mesh and weights state
  CC0 in their own files; the face targets and rig carry no per-file notice and are taken as the MakeHuman data's CC0.
