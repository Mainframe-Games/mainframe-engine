# ADR 0093 — Game code reload through a collectible `AssemblyLoadContext`

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (E4 code reload, engine side; lane m10b)

## Context

The editor must load a game's node types, then unload them and load a rebuilt assembly without restarting. A
collectible context is only collected when nothing references its types; the engine had process-wide caches keyed by
type or assembly.

## Decision

- `GameAssemblyLoader` loads the game assembly (and its private dependencies) from **in-memory copies** (so the build
  can overwrite the files) into a collectible context; the engine and anything the host already has resolve to the
  default context (one `TypeRegistry`, one `Node`).
- Unload forgets everything that pins game types: `TypeRegistry.UnregisterAssembly` (also drops the "examined"
  marker; collectible assemblies are now remembered in a `ConditionalWeakTable`), `ReplicationRegistry`,
  `ResourceLoader.ReleaseTypesOf` (evicts cached resources of game types and drops cached scenes' inline-resource
  tables holding them), and `Node`'s per-type callback cache (weak for collectible types). Then
  `AssemblyLoadContext.Unload` and a GC loop until a weak reference to the context dies or a timeout (reported, not
  thrown).
- `Load()`/`Reload()` return nothing and the unload work runs in a non-inlined helper: in Debug builds the JIT keeps
  temporaries alive to the end of a method, and a returned `Assembly` (or a property read) in the caller's frame
  kept the context alive. Tests touch game types only inside `[MethodImpl(NoInlining)]` helpers for the same reason.
- Scenes naming types the new build lacks load as `MissingNode` (already in M2) and re-save byte-identically.

## Consequences

- The editor must free all nodes of game types (serialize open scenes first) before unloading; `Unload` returning
  false means a leak to investigate (`LastUnloadedContext` stays alive).
- Game-registered `RemovedNodeTypes` upgrades or custom `Codecs.Register` calls are not undone (they are rare;
  documented).
