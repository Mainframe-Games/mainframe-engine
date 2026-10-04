# Proposal: Networking & Replication

**Milestone:** M5 · **Status:** 🟨 partly shipped · **Depends on:** [Node system](../scene-graph-and-nodes.md) (NodeId registry)

> **Shipped (M5 scaffold lane):** the message layer, the transport abstraction, the peer-lifecycle fixes, pooled
> span-based buffers and the osx-arm64 ENet native. These are documented in [Networking](../networking.md). Still
> planned here, because it needs the M2 node system: replication (NodeId registry, spawn/despawn, snapshots,
> interpolation, RPCs), the Sandbox demo and the README update.

## Problem

The ENet layer can connect and send raw bytes, but:

- There is no message framing or dispatch, and `Read` is a hard-coded test print.
- Nothing replicates node state.
- Peers leak on disconnect.
- Public APIs are `internal`.
- ENet doesn't load on Apple Silicon.

See [Networking](../networking.md).

## Goals

- A typed **message protocol** with a header, registry and handlers.
- **Connection lifecycle events** exposed to games (`PeerConnected`, `PeerDisconnected`, `MessageReceived`).
- **Node replication:** spawn/despawn, owner/authority, and property snapshots over unreliable channels,
  with RPCs over reliable channels.
- A **transport abstraction**, so ENet and Steam Networking Sockets can be swapped.
- ENet running on osx-arm64.

## Non-goals (for now)

Client-side prediction, lag compensation, and interest management beyond "everything".

## Proposed design

### Layers

```mermaid
flowchart TB
    G["Game code: [Replicated] properties, RPCs, events"] --> R["ReplicationSystem<br/>NodeId registry · snapshots · spawn/despawn"]
    R --> M["MessageBus<br/>header · registry · dispatch"]
    M --> T{"ITransport"}
    T --> E["EnetTransport"]
    T --> S["SteamSocketsTransport"]
```

### Packet header

| Field | Type | Notes |
|---|---|---|
| `protocolVersion` | byte | reject on mismatch at connect |
| `messageId` | ushort | `MessageRegistry` assigns ids from a type list |
| `tick` | uint | server tick for snapshots |
| payload | bytes | `INetworkTransferable.NetworkWrite` |

### Channels

| Channel | Flags | Use |
|---|---|---|
| 0 | `Reliable` | spawn/despawn, RPCs, control |
| 1 | `None` (unreliable sequenced) | state snapshots |

### Replication flow

```mermaid
sequenceDiagram
    participant S as Server ReplicationSystem
    participant C as Client ReplicationSystem
    S->>C: Spawn{NodeId, typeId, owner, initial state} (reliable)
    loop every net tick (e.g. 30 Hz)
        S->>C: Snapshot{tick, [NodeId, dirty fields…]} (unreliable)
        C->>C: buffer, interpolate between last two snapshots
    end
    C->>S: Rpc{NodeId, methodId, args} (reliable, owner only)
    S->>C: Despawn{NodeId} (reliable)
```

### Fixes folded in

- Remove peers on Disconnect and Timeout, and use `TryAdd` or replace on connect.
- Make the public construction APIs `public`. Log through `Log`.
- Reference-count `ENet.Library` initialization.
- Make `NetBufferPool` thread-safe or document it as main-thread-only. Fix the double `MemoryStream`
  and add a double-return guard.

### osx-arm64

Build ENet from source as a universal dylib (`lipo` x86_64 + arm64) and place it in
`runtimes/osx-arm64/native`. Alternatively, move to a maintained ENet package; that is a dependency
decision and must be discussed first.

## Task list

- [x] `ITransport`; `EnetTransport` (fix the peer lifecycle), plus `LoopbackTransport`; `SteamSocketsTransport` is a stub
- [x] `MessageRegistry` + header + dispatch; delete the test `Read` handlers
- [x] Public events, on `MessageBus` (`NetworkNode` exposes its `Server`/`Client` buses)
- [ ] Spawn/despawn on top of the M2 `NodeId` registry (`SceneTree.Find(NodeId)`, `NodeAdded`/`NodeRemoved`) and `PackedScene` (spawn by scene UID)
- [ ] Snapshot serialization: `[Replicated]` through the M2 source generator — add a per-member collection to its
  `TypeModel` and a replication emitter beside `RegistrationEmitter` (see [Scene serialization](../scene-serialization.md#source-generator))
- [ ] Client interpolation buffer
- [ ] RPCs
- [x] osx-arm64 ENet native (universal dylib from `natives.yml`; package natives excluded)
- [ ] Sandbox: listen-server demo with two windows
- [ ] Fix the README networking example

## Open questions

- Should `[Replicated]` use a source generator (compile-time) or reflection (simpler)?
- Server-authoritative only, or allow client authority per node?

## Related

[Milestones](../../milestones.md) · [Networking](../networking.md) · [Steamworks integration](steamworks-integration.md)
