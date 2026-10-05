# ADR 0011 — JSON scene files, no binary bake (for now)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M2 (W2 lane A)

## Context

The scene-serialization proposal left two open questions: JSON or a Godot-like custom text format, and
whether shipped builds should bake scenes to a binary format. Requirements: text, diff- and merge-friendly,
no new dependency, readable/writable by the runtime and the editor, stable references.

## Decision

1. **JSON via `System.Text.Json`** (`.mscene`, `.mres`): `Utf8JsonWriter` (indented, UTF-8) to write,
   `JsonDocument` to read (comments and trailing commas accepted for hand edits). Small DTOs (`.meta`
   sidecars, `assets.index.json`) use a source-generated `JsonSerializerContext`; node/resource properties go
   through generated typed accessors and per-type `ValueCodec<T>`s, not the reflection serializer.
   - Only non-default values are written (diff against a pristine instance of the type / sub-scene).
   - One value per line for vectors (the indented writer's array layout) — verbose, but line-based diffs
     touch only changed components.
   - Nested scene instances store UID + path hint, overrides keyed by node path, and added children.
   - UIDs are a kind prefix + 12 hex digits (48 bits; the sketch had 8) to make accidental collisions across
     branches negligible.
2. **No binary bake in M2.** Loading the 1 000-node benchmark scene (serialize + parse + instantiate) takes
   ~2.6 ms on an M-series Mac; scene loading is not a bottleneck. A bake can be added behind
   `ResourceLoader` later without changing the format or the API (the file `format` number allows it).

## Consequences

- Zero new dependencies; tooling (jq, editors, diff) works on scene files.
- Merge conflicts in large scenes are possible; revisit a custom format if they become painful.
- Load-time allocation is noticeable (~3.4 MB for 1 000 nodes) — acceptable at load time, never per frame.

## Amendment (2026-10-05)

Amended by [ADR 0100](0100-mobile-strategy.md#amendment-to-adr-0011-cooked-binary-exports). Source and editor data
stay JSON, as above. **Exports** (the mobile `mf-cook` step) may cook meshes and scenes to binary build artefacts.

## Amendment (2026-10-05, layout)

Amended by [ADR 0102](0102-scene-format-2.md): format 2 lists nodes flat with parent paths, keys inline resources by
stable `Type_xxxxx` ids instead of discovery-order numbers, and keeps short arrays on one line (replacing the
one-value-per-line vectors above).
