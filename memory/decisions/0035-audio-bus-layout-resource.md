# ADR 0035 — Bus layout as an `.mres` resource (until `project.mfproj`)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M7

## Context

The proposal stores the bus layout in `project.mfproj`, which only arrives with the editor (M10). M7 still needs a
persisted layout (names, sends, volume, mute, solo, voice pools, effects).

## Decision

- `AudioBusLayout : Resource` with `List<AudioBusInfo>` (also resources), each with `List<AudioEffect>` (abstract
  resource; `AudioEffectLowPass`, `HighPass`, `Reverb`, `Compressor` create SoundFlow modifiers). Serialized by the
  M2 generator/`ResourceSaver` like any resource: a normal `.mres` with a `res_` UID, diffable JSON.
- Default location `Content/Settings/AudioBusLayout.mres` (`AudioOptions.BusLayoutPath`); missing or invalid →
  `AudioBusLayout.CreateDefault()` (Master 16 voices → Music 4, SFX 32, UI 8, Voice 8) with an error logged for
  invalid files. Validation: Master first, unique names, sends name an earlier bus (a tree), pool sizes 0–1024.
- `AudioServer.GetBusLayout()` snapshots live state, `SaveBusLayout()` writes it, `ApplyBusLayout()` swaps a new
  graph in at runtime.

## Consequences

- M10 can embed the same resource inline in `project.mfproj` (or keep referencing the file by UID) without format
  changes; the editor's Audio panel edits `AudioBus` live and saves through `SaveBusLayout`.
- Effect parameters are applied when a layout is applied; live parameter automation is future work.
