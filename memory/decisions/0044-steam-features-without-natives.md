# ADR 0044 — Steam features that need natives stay designed, not implemented

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M5 (W3 lane m5-repl)

## Context

The `steam_api` native libraries come from the Steamworks SDK (partner login, a user action), and Steamworks.NET
2024.8.0 ships x86-64-only managed assemblies, so Steam cannot start on any machine this build runs on (Apple
Silicon cannot even load the assembly). M5 asked for `SteamSocketsTransport` and lobby avatars as textures "only if
natives are loadable".

## Decision

- **Not implemented:** `SteamSocketsTransport` stays a documented stub (`IsAvailable` false; `TryListen`/`TryConnect`
  fail) and avatars as Vulkan textures / ImGui `TextureId` are documented only. Code that cannot be run or tested
  against a live Steam client is not written speculatively.
- **Implemented anyway:** everything that does not need Steam running — the engine hook (`SteamServer`: init,
  per-frame `RunCallbacks` via the scene tree's frame servers, shutdown), the lobby `connect` metadata and the
  transport handoff (tested over loopback), wrappers that no-op without Steam.
- M5 is marked ✅ in the milestones with a footnote naming these limitations.

## Consequences

- Follow-up when natives are available (user action): add the SDK files under `MainframeEngine/runtimes/<rid>/native`,
  implement `SteamSocketsTransport` (`ISteamNetworkingSockets`, SteamID peers, reliable/unreliable lanes, the
  registry fingerprint as connection user data) and avatars via `SteamUtils.GetImageRGBA`.
- Apple Silicon additionally needs an AnyCPU/arm64 Steamworks.NET (a dependency decision).
