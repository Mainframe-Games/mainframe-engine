# ADR 0259 — NativeAOT exports stay in G19, built through `MainframeNativeAot`

- **Date:** 2026-11-07
- **Status:** accepted
- **Docs:** [Protected exports](../../docs/design/protected-exports.md#nativeaot-phase-4),
  [Game export](../../docs/design/game-export.md), [ADR 0239](0239-protected-exports.md), [ADR 0229](0229-export-runner-in-csharp.md)

## Context

G19's phase 4 is the `nativeAot` preset key: shipped game code becomes machine code, which also buries the pack key
(ADR 0240) in machine code instead of IL. The proposal left open whether it stays in G19 or moves to M12.6 (mobile's
trim/AOT work); the owner answered on 2026-11-07: do it now in G19, and M12.6 and C0 reuse it.

A spike publish of the template game showed what the proposal's "known sites" list missed and what its
`-p:PublishAot=true` would break:

- `PublishAot` as a global property reaches every referenced project, including the netstandard2.0 generator
  (NETSDK1207: AOT not supported for the target framework) in a checkout build.
- `Node` finds `OnProcess`/`OnInput`… overrides by reflection on the runtime type; NativeAOT keeps no metadata for them,
  so no node processed and the game never left its loading screen.
- The Steamworks shim is resolved from `steamworks/<os>/` at run time (`AssemblyLoadContext.Resolving`), which
  NativeAOT cannot do: `SteamApi.*` "will always throw".
- A macOS release bundle starts the app through an apphost rewritten to `../MonoBundle/<app>.dll`; a NativeAOT
  executable has no app `.dll`, and its base directory is its own folder.

## Decision

- **Phase 4 stays in G19.** M12.6 and C0 build on it (their "trim/AOT analyzers clean" and "desktop NativeAOT smoke"
  are done here for desktop).
- **The engine is AOT-clean by construction:** `IsAotCompatible` on `MainframeEngine` and the Steamworks shims, with
  every trim and AOT diagnostic an error in `MainframeEngine/Src` (previously only `Src/Saves`). Reflection over node
  types is kept with a type-level `[DynamicallyAccessedMembers]` on `Node` (methods and public properties of every node
  type survive trimming), not by moving override detection into the generator: unregistered node types (the editor's,
  tests') keep working, and a game needs nothing new.
- **`mf-export` passes `-p:MainframeNativeAot=true`, not `-p:PublishAot=true`.** `MainframeGame.props` (imported by
  every game project, the package's `buildTransitive` copy too) turns it into `PublishAot` for the game's own projects
  only, and with it:
  - the target OS's `MainframeEngine.Steamworks` and `Facepunch.Steamworks.*` assemblies are handed to ILC as
    references, so they are compiled into the executable; `SteamworksLoader`'s resolver then never runs, and the
    `steamworks/` folder is not published;
  - the macOS bundle apphost step (`BundleAppHost.targets`) is skipped.
- **macOS bundles keep their layout.** The NativeAOT executable alone goes in `Contents/MacOS`, everything else in
  `Contents/MonoBundle` as for JIT builds. At start-up, a NativeAOT process whose executable sits in
  `X.app/Contents/MacOS` next to a `MonoBundle` folder sets the `APP_CONTEXT_BASE_DIRECTORY` AppContext data to that
  folder before anything reads `AppContext.BaseDirectory`, so content, natives and the pack are found as in a JIT bundle.
- **Validation:** NativeAOT cannot cross-compile between operating systems, so `native-aot-host` is an error when the
  host OS is not the target's; `readyToRun`, `singleFile`, `trim` and `selfContained: false` are ignored with a warning
  (`native-aot-ignored`) when set away from their defaults.
- **Third-party warnings:** Silk.NET and Microsoft.Extensions.DependencyModel produce ILC warnings in code the engine
  never runs (Silk's deps.json native-path resolver; the engine installs its own). `MainframeGame.props` keeps those
  codes (IL2104 for assemblies with collapsed trim warnings, IL3000, IL3002) from failing a game's warnings-as-errors
  publish (`WarningsNotAsErrors`), so they stay visible; the template smoke fails on any ILC warning outside that known
  set.

## Ruled out

- **`-p:PublishAot=true` on the command line** (the proposal's wording): breaks checkout builds through the generator
  project, and would turn on AOT analyzers in the tools a game's build references (mf-l10n).
- **Generated override flags** (`NodeTypeInfo` carrying which callbacks a type overrides): a second source of truth,
  and unregistered node types would silently stop processing.
- **A separate AOT bundle layout** (natives in `Contents/Frameworks`, content in `Contents/Resources`): every content
  and native path would differ between JIT and AOT builds of the same game.
- **Loading the Steamworks shim at run time under AOT:** impossible (no assembly loading in NativeAOT).

## Consequences

- Engine code that reflects over runtime types must say what it needs (`[DynamicallyAccessedMembers]`) or fail the build.
- A game's own reflection shows up as analyzer errors in its AOT export (its projects get `PublishAot`), not as a
  broken build at run time.
- NativeAOT executables are larger and take minutes to compile; `nativeAot` stays opt-in (default false).
- CI's `native-aot` job (owner, 2026-11-07) exports the template game with `nativeAot` on each OS runner and runs it
  headless, part of `ci-success`: every code PR pays a few minutes of ILC per OS.

## Revisit when

- Silk.NET 3 (or a later 2.x) stops producing the third-party warnings: drop them from `WarningsNotAsErrors`.
- Consoles (C0) or mobile (M12.6) need a static-library (`NativeLib=Static`) output.
