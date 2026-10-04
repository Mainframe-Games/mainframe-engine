# ADR 0090 — `project.mfproj` as hand-written versioned JSON; `GameHost` + `GameSession`

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E4, engine side; lane m10b)

## Context

The editor proposal needs a project file (name, main scene, window, physics tick, input map) and a host that runs a
project without an `Engine` subclass, for play mode and for shipping. Open choices: serialize the settings as an M2
resource (`.mres` machinery) or as their own JSON; where the input map, bus layout and localization settings live;
how the host is tested without a GPU.

## Decision

- `ProjectSettings` is a plain C# model written by hand (`ProjectSettingsFormat`) with `Utf8JsonWriter` /
  `JsonNode`, in the scene-format conventions: UTF-8, LF, indented, comments and trailing commas tolerated, only
  non-default values below the identity (`format`, `name`, `engineVersion`). No reflection, trimming-safe, readable.
- `format` (currently 1) + `ProjectMigration(From, Action<JsonObject>)` steps run on the JSON tree before reading;
  newer formats are rejected. Unknown keys warn (forward-compatible hand edits); wrong types throw
  `InvalidDataException` naming the file and the key path.
- The **input map** is a section of the project file with one string per binding (`key:Space`, `pad1:A`,
  `axis:LeftX-`) — not an `.mres` of binding resources: diffable, hand-editable, and the runtime `InputMap` stays
  a small allocation-free structure. The **audio bus layout** stays its own `.mres` (ADR 0035) referenced by path or
  UID. **Localization** mirrors M9's `LocalizationOptions` fields (wired when M9 integrates). **Shadow quality**
  `Off` disables shadow maps today; Low/Medium/High map to an atlas budget (`ShadowAtlasSize`) for M4's planner.
- `GameHost : Engine` (non-abstract, not sealed) maps settings to `EngineOptions`, applies the rest after
  `base.OnLoad()`, and delegates to `GameSession`, which works on any `SceneTree` (autoloads, start scene, editor
  commands) so it is unit-tested headless. `GameHost.Run(args, assemblies)` is the one-line launcher.
- Autoloads are a scene or a registered type, added under `/root` in order before the main scene; a broken one is
  logged and skipped.

## Consequences

- Adding a setting = model property + reader + writer + test; layout changes bump `format` with a migration.
- Engine version compatibility is advisory (warning on a different major/minor; dev builds never warn).
- The Sandbox keeps its `Engine` subclass; both paths are supported.
