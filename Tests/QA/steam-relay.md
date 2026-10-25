# Steam relay sockets: manual QA

`SteamSocketsTransport` (docs/design/networking.md#steam-relay-sockets) is unit-tested over a fake Steam
(`SteamSocketsTransportTests`). A real relay join cannot run in CI or on one machine with one account: one Steam
account cannot relay-connect to itself. Run this before a release that touches `SteamSocketsTransport`, the
`SteamworksSockets` shim or `SteamSocketsApi`, and after refreshing Facepunch.Steamworks or the `steam_api` natives.

## Setup

- Two machines (or one machine plus a VM), each logged in to a **different** Steam account; the two accounts are Steam
  friends. Ideally on different networks (a phone hotspot for one), so the join really goes through Valve's relay.
- The same build of a game on both, with `steam.appId` set in `project.mfproj` (480, Spacewar, works for testing) and
  `devAppIdFile` on (any Debug build), a host flow and a join flow (below). The Steam client is running on both.
- Debug logging on both (`Log` at debug level), so the `[Net]` lines show up in the console or log file.

The game needs: a "Host" action that does

```csharp
ITransport server = EnetTransport.Listen(7777, 8);
if (SteamSocketsTransport.TryListen(0, 8, out var steam))
    server = new MultiServerTransport(steam, server);
mp.StartServer(server);
var connect = NetworkAddress.FormatList(NetworkAddress.Steam(Steam.SteamId), NetworkAddress.Enet(NetworkNode.LocalIp, 7777));
await SteamLobby.CreateLobbyAsync("QA", 8, connectAddress: connect);
SteamOverlay.InviteFriendToGame(connect);
```

and joins from `SteamLobby.OnLobbyJoined` (`mp.TryConnect(lobby.ConnectAddress)`), plus something that sends a reliable
RPC and something replicated every tick (a moving `[Replicated]` node is enough).

## Steps

1. **Host.** Machine A: Host. Expect `[Net] Steam relay host listening on virtual port 0 (max 8 peers)` and the ENet
   line. `Steam.Valid` is true.
2. **Invite and join.** Machine A invites B from the overlay; B accepts. Expect on B: `connecting through
   steam:<A's id>`, `Steam relay client connecting to <A's id>`, then `joined the server as …`. On A:
   `Steam peer connected …` with B's SteamID and `client connected`. Not `enet:` (that would mean the Steam attempt
   failed; look for `cannot use 'steam:…'`).
3. **Reliable traffic.** Trigger the reliable RPC both ways several times: every call arrives once, in order.
4. **Unreliable traffic.** Watch the replicated node on B for 30 s: it moves smoothly; the network panel of the dev
   overlay (F12) shows snapshots applied and bytes per second on both sides.
5. **Bad network.** On B call `SteamSocketsTransport.SimulateNetworkConditions(150, 10)` (from a debug key or console):
   movement stays smooth behind the interpolation delay, RPCs still arrive (later). Set it back to `(0, 0)`.
6. **Kick.** On A, `mp.Kick(peerOfB)`: B logs a disconnect with `Kicked` and frees the host's nodes.
7. **Leave.** Join again (new peer id on A), then quit B normally: A logs `Steam peer disconnected …: Shutdown`.
8. **Host quits.** Join again, then quit A: B logs `Shutdown` and goes offline.
9. **Timeout.** Join again, then pull B's network cable (or kill its Steam client): A reports B as `Timeout` within
   about 10 s.
10. **Fallback.** On B quit Steam, restart the game outside Steam and join with A's connect string (paste it, or
    `mp.TryConnect(connect)`): expect `cannot use 'steam:…': Steam is not running` and an ENet join (same LAN).
11. **Own account.** On A start a second instance and `TryConnect` the connect string: expect `it is this Steam account
    (one account cannot relay-connect to itself)` and an ENet join.

Host migration is not in scope. Report the OS pair, the network setup and any step that did not behave as described.
