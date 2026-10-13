# ADR 0189 — Facepunch.Steamworks replaces Steamworks.NET; the steam_api natives ship

- **Date:** 2026-10-12
- **Status:** accepted (supersedes ADR 0044's natives and Apple Silicon consequences; its "transport stays a stub" part
  still holds)
- **Milestone:** M5 follow-up, distribution (#48)
- **Docs:** docs/design/steamworks.md, docs/design/release.md (Games), docs/design/distribution-nuget.md

## Context

Steam never started anywhere. Steamworks.NET 2024.8.0 ships x86-64-only managed assemblies (Apple Silicon cannot load
them), and no `steam_api` native shipped (ADR 0044). The studio's games already use Facepunch.Steamworks: Speed Golf
vendors 2.4.1 with Valve's redistributables and runs on Windows, macOS (arm64 included) and Linux. Games that use the
engine package (Crash Site Defense, Buy My Snake Oil: Steam app 2202980) need Steam and player builds on all three.

## Decision

- **Facepunch.Steamworks 2.4.1, vendored** in `Plugins/Facepunch.Steamworks` (Win64 and Posix builds, as Speed
  Golf has them). Steamworks.NET is removed from the central package versions.
- **A shim, built twice.** Facepunch's Win64 and Posix builds differ in assembly name, imported library name and
  struct packing, and the engine ships one assembly for every RID. `MainframeEngine.Steamworks` (internal, primitive
  API, no engine reference) is compiled once against each under one assembly name. Both builds go to
  `steamworks/{win64,posix}/` beside the app (never the app directory itself), and `SteamworksLoader` resolves this
  OS's through `AssemblyLoadContext.Resolving`. One mechanism covers checkout builds, package builds, RID-less runs and
  per-RID publishes. Rejected: NuGet `runtimes/<rid>/lib` assets (a second mechanism for checkout builds, and RID-less
  builds of project references get no RID assets); compiling the engine per platform (one package for every RID).
- **The natives are committed and packed** (`runtimes/<rid>/native`, the universal dylib for both macOS RIDs), from
  the SDK version Facepunch 2.4.1 was built against. They are vendored, outside `natives.lock`.
- **Native `setenv` for the app id on macOS and Linux**, because .NET's environment never reaches steam_api's `getenv`
  there. Steam starts the same way on all three OSes.
- **Speed Golf's lifecycle lessons are rules:** callbacks pumped by the engine (`asyncCallbacks: false`), events
  subscribed after init and removed on shutdown, lobby left and presence cleared before `SteamClient.Shutdown`,
  `Dispatch.OnException` logged, `Dispatch.OnDebugCallback` never set.
- **Calls Facepunch keeps internal** (invite dialogs) use the steam_api flat API from the shim, not reflection.
- **#48 in the same change:** the package ships `tools/package-game.sh` (+ `tools/macos/png-to-icns.sh`), found
  through `MainframePackageGameScript` from `MainframeGame.props`. The script prunes other platforms' Steam files and
  refuses a `steam_appid.txt`. The package-mode template smoke packages linux-x64, win-x64 and osx-arm64 and checks
  each archive.

## Consequences

- `Steam.IsPlatformSupported` is Windows x64, Linux x64 and macOS x64/arm64. `SteamTests` really initialize on every CI
  OS (`SteamNotRunning` there; `Running` as Spacewar on a developer machine with Steam open).
- A Facepunch upgrade means refreshing the Win64/Posix DLLs and the natives together (flat-API interface versions).
- Still to do: `SteamSocketsTransport` on Facepunch relay sockets and avatar textures. Both are now possible.
- The package carries assemblies outside `lib/` (`mainframe/steamworks/`), so `NU5100` is suppressed for the engine
  project.
