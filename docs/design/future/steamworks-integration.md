# Proposal: Steamworks Integration

**Milestone:** M5 · **Status:** 🟨 partly shipped

> **Shipped (M5 scaffold lane):** see [Steamworks](../steamworks.md).
> - The engine-owned `Steam` service: `TryInitialize`, `RunCallbacks`, `Shutdown`, with no Unity code and no
>   preprocessor gate.
> - Dev `steam_appid.txt`.
> - `Valid` guards on every wrapper.
> - Every lobby fix in the table below, plus `LobbyChatUpdate_t` member events.
> - `StoreStats` and `RequestCurrentStats`.
> - `SteamRemotePlay` and `SteamAvatar` deleted.
>
> **Still open:**
> - The `Engine` hook and `EngineOptions.SteamAppId`.
> - Native packaging: blocked, because the SDK needs a partner login, and Steamworks.NET 2024.8.0 is x86-64 only, so
>   osx-arm64 is unsupported.
> - Avatars.
> - The transport handoff.
> - The README.

## Problem

The Steam wrappers never run:

- `SteamManager` is a Unity leftover that compiles to `Initialized => false`.
- No `steam_api` native library ships, and callbacks are never pumped.
- The lobby code has bugs that would surface as soon as it ran.

See [Steamworks](../steamworks.md).

## Goals

- Steam starts only when available (Steam client running and the native library present), and the game
  continues without it otherwise (CLAUDE.md: "only activate if Steam is running").
- Callbacks are pumped once per frame from `Engine`.
- Lobbies work end to end, including invites and the ENet or Steam-sockets handoff.
- Avatars are available as Vulkan textures. Achievements persist.

## Proposed design

```mermaid
stateDiagram-v2
    [*] --> Probing: Engine.OnLoad (EngineOptions.SteamAppId set)
    Probing --> Unavailable: steam_api missing / SteamAPI.Init false
    Probing --> Running: SteamAPI.Init true
    Running --> Running: Engine.OnUpdate → SteamAPI.RunCallbacks()
    Running --> Shutdown: Engine.OnClose → SteamAPI.Shutdown()
    Unavailable --> [*]
    Shutdown --> [*]
```

- Replace `SteamManager.cs` with a small engine-native `SteamManager` (no Unity, no preprocessor gate).
  Catch `DllNotFoundException` during init.
- `EngineOptions.SteamAppId` (nullable). In development, write `steam_appid.txt` next to the executable.
- Ship `steam_api64.dll`, `libsteam_api.so` and `libsteam_api.dylib` (from the Steamworks SDK, under
  its license) through a `runtimes/` folder. osx-arm64 depends on SDK support.
- Guard every wrapper with `Steam.Valid`.

### Lobby fixes

| Bug | Fix | Status |
|---|---|---|
| Invite joins `Current` | join `callback.m_steamIDLobby` via `JoinLobbyAsync` | ✅ (leaves `Current` first) |
| Create failure hangs | `TrySetException` / `TrySetResult(null)` | ✅ result 0 → `null` |
| `SetResult` throws on a second enter | `TrySetResult`; check `m_EChatRoomEnterResponse` | ✅ |
| Global `Callback<T>` for requests | `CallResult<T>` for `CreateLobby` and `RequestLobbyList` | ✅ (and `JoinLobby`) |
| `HostId` parse | `ulong.TryParse` | ✅ |
| No timeouts | `CancellationToken` + timeout on all async calls | ✅ `SteamLobby.Timeout` (15 s) |
| `appVersion` optional but required | store it only when given | ✅ |
| Unguarded `Branch`, `GetFriends`, lobby list, `SteamLobbyInfo` | `Steam.Valid` guards | ✅ |

### Transport handoff

Lobby metadata carries `connect = "enet:ip:port"` or `"steam:<steamId>"`. The
[networking transport](networking-replication.md) picks `EnetTransport` or `SteamSocketsTransport`.

### Avatars

`SteamUtils.GetImageRGBA` → `GpuTexture` (from [GPU resources](../gpu-resources.md)).
Expose it to ImGui once the ImGui controller supports `TextureId`.

## Task list

- [x] Engine-native service (`Steam.TryInitialize`/`RunCallbacks`/`Shutdown`); [ ] call it from `Engine`
- [ ] Native library packaging (blocked: SDK needs partner login; osx-arm64 unsupported by Steamworks.NET 2024.8.0) · [x] `steam_appid.txt` for development
- [x] Guard every wrapper with `Valid`
- [x] Lobby fixes (table above) + `LobbyChatUpdate_t` member events
- [x] `SteamAchievements.StoreStats` + `RequestCurrentStats`
- [ ] Avatars → textures; ImGui `TextureId` support
- [x] Delete `SteamRemotePlay` or port it (deleted, with the commented-out `SteamAvatar`)
- [ ] Update the README claims

## Open questions

- Is Steam Networking Sockets the primary transport for shipped builds?
- How do we handle osx-arm64 until Steamworks.NET ships arm64 assets?

## Related

[Milestones](../../milestones.md) · [Steamworks](../steamworks.md) · [Networking & replication](networking-replication.md)
