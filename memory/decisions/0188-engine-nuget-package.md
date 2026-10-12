# ADR 0188 — The engine ships as the `MainframeEngine` NuGet package

- **Date:** 2026-10-12
- **Status:** accepted (supersedes the "package mode is future" part of ADR 0094)
- **Milestone:** distribution
- **Docs:** docs/design/distribution-nuget.md

## Context

A game could only use the engine through a checkout of this repo: the template defaulted to project references, and
the released editor's New Project needed a checkout too (it ships no engine sources). Games should consume a
versioned engine with `dotnet add package`, and the editor should be an optional download.

## Decision

- **Two packages on nuget.org, `MainframeEngine` and `MainframeEngine.Templates`.** The repo is public and the ids
  were free. Both are pushed by `publish.yml` after the GitHub Release exists, at the release version. GitHub Packages
  was rejected because consumers would need a PAT even for a public repo.
  - Publishing uses nuget.org Trusted Publishing (GitHub OIDC → a 1-hour API key from `NuGet/login`), so no long-lived
    key is stored or rotated. The policy is owned by the `Mainframe-Games` organization, which therefore owns both ids.
  - The packed template defaults its engine version to its own version, so `dotnet new install
    MainframeEngine.Templates` + `dotnet new mfgame` needs no options.
- **Everything a game needs is inside the package:**
  - `lib/` holds the engine and **spine-csharp**. Spine is bundled, not a package of its own: its reference is private
    only while packing (`_IsPacking`), so checkout projects still compile against `SpineNode`'s Spine types.
  - `analyzers/` holds the generator.
  - `runtimes/<rid>/native` holds the natives, resolved by NuGet with no flat copy.
  - `tools/mf-l10n` holds the localization tool, managed files only, run without a `deps.json`.
  - `buildTransitive/` holds the checkout's own `MainframeGame.props`, `Localization.targets` and `macos/` files, so
    package and checkout games build the same way.
- **Engine content goes into the game's own `Content/`.** It is packed as `mainframe/Content/**` from the engine's
  copy-to-output items, so there is no second list, and copied into app projects only. `ContentPaths` is unchanged.
  The proposal's `Content/Mainframe/` subfolder was rejected: it would have changed every engine content path and
  checkout builds too, for a collision that has not happened.
- **Defaults.**
  - The template defaults to `--engine-source package`; `project` remains for engine work.
  - The template opts out of an enclosing `Directory.Packages.props`.
  - The editor publishes the template next to itself. Its New Project defaults to the package at its own version and
    falls back to a checkout in development builds.
- **Doc comments.** `GenerateDocumentationFile` is on for the package's IntelliSense, with CS1591, CS1573 and CS0419
  suppressed. The existing broken crefs and misplaced comments were fixed.

## Consequences

- Packing goes through `build/pack-nuget.sh`. `mf-l10n` is built first because it references the engine, so the
  engine's pack cannot build it; then the engine and the templates are packed.
- CI's `template` job also packs, then builds, runs and publishes a game against the package on lavapipe. The release
  needs only the trusted publishing policy on nuget.org: no secrets (`NUGET_USER` in the workflow names its creator).
- The package is about 31 MB: trees content (14 MB), natives for 4 RIDs, and mf-l10n's managed dependencies.
- Not done yet:
  - an "upgrade project" action for package games;
  - package-based Demo/Forest downloads;
  - `MainframeEngine.Sdk`.
