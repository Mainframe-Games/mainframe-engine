# ADR 0094 — `mfgame` template references the engine by path until packages ship

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10b)

## Context

`dotnet new mfgame` should create a project that builds today, but the NuGet packages
([distribution plan](../../docs/design/future/distribution-nuget.md)) are future work.

## Decision

- The template's `Directory.Build.props` holds `MainframeEnginePath` (`--engine-path`, absolute or relative to the new
  project); `MyGame.csproj` project-references `MainframeEngine` and the generator (as an analyzer). Engine content and
  natives reach the launcher's output the same way they reach the Sandbox's.
- `--engine-source package` emits a `PackageReference Include="MainframeEngine" Version="--engine-version"` instead
  (template conditionals); it is documented as future and not tested in CI.
- The solution is a `.slnx` (SDK 10; no GUIDs to regenerate). The template is consumed from source
  (`dotnet new install <folder>`) and packs into `MainframeEngine.Templates` (`just template-pack`), not published.
- CI job `template` (required by `ci-success`) installs it into a private hive, builds a game warnings-as-errors and
  runs its launcher on lavapipe.

## Consequences

- Game projects inherit nothing from the engine repo's build props; the engine projects keep theirs.
- Switching a game to packages later is a one-line change in its `Directory.Build.props`/csproj.
