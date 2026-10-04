# ADR 0100 — Mobile strategy: MoltenVK first, Android 10+/iOS 16+ arm64, thin native shims, M12/M13

- **Date:** 2026-10-05
- **Status:** accepted (design; implementation starts with the M12.0 spikes)
- **Milestone:** M12 (mobile core), M13 (mobile platform services)
- **Spec:** docs/design/future/mobile.md, docs/design/future/mobile-services.md

## Context

The user wants the engine on phones and tablets with the same 3D + 2D feature set as desktop. The engine today:

- is a Vulkan 1.2 renderer on Silk.NET 2.22 (SDL2 windowing), with MoltenVK on macOS;
- uses pure-C# physics, SoundFlow/miniaudio audio, and RmlUi + ENet through engine-built natives;
- has a source-generated type registry (no reflection), and an editor with a TCP editor link and collectible game
  assemblies.

Constraints found while researching (2026-10-05, sources in the design docs):

- **Android.** Vulkan 1.1 is required on 64-bit devices since Android 10, and 62 % of active devices report only
  1.1. Google recommends Android 10+, Vulkan 1.1 and the 2022 Android Baseline Profile. Play requires target API 36
  and 16 KB page support (updates blocked from 1 Feb 2027). Silk.NET's Android SDL binaries are 4 KB aligned.
- **iOS.** No JIT. NativeAOT on iOS has been supported since .NET 9. MoltenVK uses only public APIs (App Store safe)
  and implements a Vulkan 1.4 subset. Uploads need Xcode 26 / the iOS 26 SDK. StoreKit 2 is Swift-only.
- **.NET 10.** Android release builds use Mono profiled AOT; NativeAOT and CoreCLR on Android are experimental. .NET 11
  moves mobile to CoreCLR.
- **Accounts.** The user has neither an Apple Developer Program membership nor a Play Console account.

## Decision

- **Targets:** Android 10+ (API 29) arm64-v8a, and iOS 16+ arm64, phones and tablets.
  - Development only: iOS Simulator arm64; Android emulator arm64 on Apple Silicon and x86_64 on Linux CI (an
    `android-x64` RID that is never shipped).
- **Rendering:**
  - iOS uses **MoltenVK**, reusing the Vulkan renderer. A native Metal backend comes only through M11's backend
    abstraction, and only if profiling demands it.
  - Android uses native Vulkan.
  - The renderer baseline becomes **Vulkan 1.1 + capability checks** on every platform: shaders target
    `vulkan1.1` (SPIR-V 1.3), with the `VP_ANDROID_baseline_2022` limits gated in CI through the Khronos Profiles layer.
  - Mobile frames use a TBDR layout: scene and tonemap merged into one render pass with transient, lazily allocated
    attachments; direct UI replay; D16 shadows; optional MSAA resolved on tile.
  - Quality tiers bundle existing knobs, with dynamic resolution and a thermal governor.
- **Host:** SDL2 through Silk.NET's mobile entry points (`SilkActivity`, `SilkMobile.RunApp`).
  - Silk.NET 2.23's SDL/MoltenVK are tried first. SDL2 (and MoltenVK) are vendored and built in `natives.yml` only
    if 2.23 fails the 16 KB/iOS checks (see the decisions recorded after review).
  - An engine-owned host (GameActivity / UIKit + `CAMetalLayer`) is the designed fallback if spike S1 fails.
  - Everything above the host goes through `IAppPlatform` seams in a core that stays `net10.0`. Platform code lives in
    `MainframeEngine.Android` / `MainframeEngine.iOS`. Games add `MyGame.Android` / `MyGame.iOS` heads from `mfgame`.
- **Runtime:**
  - Runtime-agnostic until M12 start; spike S2 picks the .NET version and runtime per platform (see the decisions
    recorded after review). Leading candidates: NativeAOT on iOS, and the SDK's production runtime on Android.
  - The engine must be `IsAotCompatible` with zero warnings. Collectible game assemblies stay editor/desktop-only:
    code changes on devices always mean rebuild + reinstall. Content changes live-preview over the editor link.
- **Content:** an export-time cook (binary export artefacts allowed: [amendment to ADR 0011](#amendment-to-adr-0011-cooked-binary-exports)). Textures become KTX2 with ASTC primary and an ETC2 fallback (via Play
  texture-format targeting). Models are cooked, so no Assimp ships on devices. Content goes into `.mfpak` packs through
  an `IContentFileSystem`, delivered with Play Asset Delivery; iOS uses bundled packs for M12.
- **Services:** thin engine-owned native shims, Swift (iOS) and Kotlin + a C++ JNI bridge (Android), behind **one
  flat, versioned C ABI** (`mfplatform`).
  - Built in `natives.yml`, following the rules of the `mfrmlui` ABI (ADR 0002).
  - Asynchronous requests with an event queue polled once per frame on the game thread.
  - Engine-side servers (`ConsentServer`, `StoreServer`, `GameServicesServer`, `CloudSaveServer`,
    `NotificationServer`, `AdsServer`, `AnalyticsServer`, `CrashReporter`).
  - Desktop backends: Steam where it maps, fake and null otherwise.
  - No generated Java/Objective-C bindings, community plugins or fastlane.
- **Pipeline:**
  - `just android-*` / `ios-*` recipes and CI jobs build unsigned/debug artefacts without any account.
  - A manual `mobile-publish.yml` (main only, actor brogan89, environment `mobile-release`) signs and uploads to Play
    internal (androidpublisher v3 over `curl` + `openssl`) and TestFlight (`xcrun altool --upload-app` with an App
    Store Connect API key, or the `buildUploads` API), using official `actions/*` only.
- **Roadmap:** mobile core is **M12** (after M11 but not dependent on it; it needs M10 for deploy, M3/M4 for tiers and
  M8 for virtual controls), starting with spikes M12.0 S1–S7. Mobile services are **M13** (after M12).

## Consequences

- Desktop is unaffected until code lands. The [mobile-ready plumbing checklist](../../docs/design/future/mobile.md#mobile-ready-plumbing-checklist)
  (also in `memory/context/lane-agent-brief.md`) asks lanes to stop adding mobile-hostile assumptions now: forever
  surfaces, single pointer, file-only content, RGBA8-only textures, JIT-only APIs, `LOAD` ops, Vulkan 1.2-only features.
- **More natives to build and lock:** SDL2, mfrmlui, enet and mfplatform for android-arm64/x64 and ios/iossimulator;
  miniaudio for the simulator.
- Lowering shaders to `vulkan1.1` changes every committed `.spv` and `shaders.lock` once. Goldens should not change
  (S6 checks).
- The dependency questions this left open were answered by the user and are recorded below.
- **Risks** (detailed in the design doc): Silk.NET mobile maturity, .NET 11 runtime changes, MoltenVK non-conformance
  for the merged pass, Vulkan 1.1 driver quality on low-end Android, Apple's 80 MB `__TEXT` limit, and store-policy
  drift (yearly target-API and Xcode-SDK bumps).

## Decisions recorded after review (2026-10-05)

The user answered the design's open questions. They are decided:

1. **SDL2 / MoltenVK natives: try Silk.NET 2.23 first.**
   - Spike M12.0 S1 bumps Silk.NET 2.22 → 2.23 on the spike branch only. It checks 16 KB alignment of 2.23's Android
     SDL (`libSDL2.so`, `libmain.so`) and its iOS support (SDL + MoltenVK 1.4.1 static libraries).
   - Only if 2.23 still fails do we vendor SDL2 and MoltenVK and build them in `natives.yml`: Android `.so` + `SDLActivity`,
     iOS xcframeworks.
   - **No package versions change now.** A bump lands only with the S1 result.
   - The research pre-check found 2.23's Android SDL still 4 KB aligned (Silk.NET #2493), so the fallback is likely for
     Android.
2. **.NET version and mobile runtime: decided at M12 start.**
   - The design stays runtime-agnostic. Engine code must stay trim- and AOT-safe from now on, which every candidate
     needs: Mono AOT, NativeAOT, CoreCLR + R2R.
   - Spike M12.0 S2 runs on the then-current .NET (likely 11 / CoreCLR) and picks the runtime per platform.
   - This supersedes the "iOS Release = NativeAOT" line above, which is now the leading candidate, not a decision.
3. **Approved for mobile exports:**
   - Cooked binary meshes/scenes in exports (see the amendment below).
   - **astcenc** (Apache-2.0) and an **ETC2 encoder** (etcpak, BSD) as **build tools only**: host tools pinned by
     source/checksum and run by `mf-cook`, never linked into or shipped with the engine or games.
4. **Platform and services:**
   - **iOS 16 stays the minimum.** Device deploy uses the iOS workload's `mlaunch` (`dotnet build -t:Run`) for iOS 16
     devices and `xcrun devicectl` for iOS 17+.
   - **Ads:** AdMob + UMP.
   - **Crash reporting:** Sentry's native SDKs (sentry-cocoa, sentry-android with NDK) behind the `mfplatform`
     services shim. This M13 dependency is approved. There is no `Sentry` NuGet; the managed engine forwards
     unhandled exceptions through the shim's `crash_*` ABI.

### Amendment to ADR 0011 (cooked binary exports)

[ADR 0011](0011-json-scenes-no-binary-bake.md) ("JSON scenes, no binary bake") still governs **source and editor
data**:

- `.mscene` and `.mres` files in a project, the editor and desktop development builds stay JSON;
- models stay glTF/FBX sources imported at load time on desktop.

**Exports may cook to binary.** The mobile export (`mf-cook`) may write:

- imported models as a `.mscene` plus binary `.mmesh` vertex/index buffers (so Assimp never ships to devices);
- scenes and resources in a binary form, when that measurably helps load time or size.

These are build artefacts:

- derived from the JSON sources, never edited or committed;
- versioned by the cooker;
- regenerated on every export.

The runtime keeps loading JSON as well.
