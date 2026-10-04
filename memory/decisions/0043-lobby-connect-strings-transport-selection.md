# ADR 0043 — Lobby connect strings and transport selection

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M5 (W3 lane m5-repl)

## Context

Open question: is Steam Networking Sockets the primary transport for shipped builds? Plan default: "ENet is the
primary transport; Steam sockets are used when Steam is present." Lobbies must tell members how to connect, and the
handoff must be testable without Steam.

## Decision

- Addresses are text: `enet:host:port` (IPv6 in brackets), `steam:<steamId>`, `loopback:<name>` (`NetworkAddress`).
  A lobby's `connect` metadata (`SteamLobbyInfo.ConnectAddress`) holds a `;`-separated list, **best first**; a host
  advertises `steam:<own id>;enet:<ip>:<port>`.
- `TransportSelector` maps schemes to `ITransportFactory`s and opens the first address that works here; factories
  return false (never throw) when unusable. `TransportSelector.Default` has ENet and Steam sockets;
  `LoopbackTransportFactory` exposes in-process servers by name (single-player listen servers, tests).
  `MultiplayerApi.TryConnect(connectString, selector)` is the handoff; because ENet (and Steam) connections complete
  asynchronously, an attempt that later fails (`ConnectFailed`, handshake timeout) falls back to the next address and
  `Disconnected` is raised only when every address failed.
- `LoopbackTransport` became multi-client (`CreateServer` + `ConnectClient`; `CreatePair` unchanged).

## Consequences

- With Steam sockets stubbed, `steam:` entries fall through to ENet; when Steam natives land, Steam-first lobbies
  start using Steam relays with no game changes.
- Tested end to end over loopback (`TransportHandoffTests`).
