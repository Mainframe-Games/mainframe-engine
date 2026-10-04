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
  - SDL2 is rebuilt by us for Android (16 KB).
  - An engine-owned host (GameActivity / UIKit + `CAMetalLayer`) is the designed fallback if spike S1 fails.
  - Everything above the host goes through `IAppPlatform` seams in a core that stays `net10.0`. Platform code lives in
    `MainframeEngine.Android` / `MainframeEngine.iOS`. Games add `MyGame.Android` / `MyGame.iOS` heads from `mfgame`.
- **Runtime:**
  - iOS Release uses **NativeAOT**; Debug uses Mono.
  - Android uses the SDK's production runtime: Mono profiled AOT on .NET 10, revisited for .NET 11 in spike S2.
  - The engine must be `IsAotCompatible` with zero warnings. Collectible game assemblies stay editor/desktop-only:
    code changes on devices always mean rebuild + reinstall. Content changes live-preview over the editor link.
- **Content:** an export-time cook. Textures become KTX2 with ASTC primary and an ETC2 fallback (via Play
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
- **Open dependency decisions** (need the user's approval):
  - Silk.NET 2.22 → 2.23 (SDL 2.32.10, MoltenVK 1.4.1);
  - vendoring MoltenVK/SDL xcframeworks;
  - texture encoders (astcenc, an ETC2 encoder) as build tools;
  - the ads provider, and Sentry for crash reporting in M13.
- **Risks** (detailed in the design doc): Silk.NET mobile maturity, .NET 11 runtime changes, MoltenVK non-conformance
  for the merged pass, Vulkan 1.1 driver quality on low-end Android, Apple's 80 MB `__TEXT` limit, and store-policy
  drift (yearly target-API and Xcode-SDK bumps).
