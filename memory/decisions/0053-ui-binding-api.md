# ADR 0053 — Managed RmlUi API shape: typed bindings, lazy documents, hot reload from sources

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M8 (W4 lane m8)
- **Spec:** docs/design/game-ui.md

## Decisions

1. **Two layers.** `MainframeEngine.UI.Rml` mirrors the C ABI closely (`RmlContext`, `RmlElement` …; borrowed objects
   as structs, callback-scoped ones as `ref struct`s so they cannot escape). The engine layer (`UiServer`, `UiLayer`,
   `UiDocument`, `UiElement`) adds nodes, lifetimes and C# events.
2. **Typed, allocation-free data binding.** `Bind<T>(name, getter, setter)` and `Bind<TOwner, T>(name, owner, static
   getter, static setter)` for scalars (`bool`/`int`/`uint`/`long`/`float`/`double`/`string`/32-bit enums), converted
   through `RmlValue<T>` (`typeof(T)` checks the JIT removes; no boxing, no reflection). The owner-state overload is
   what a `[UiBindable]` generator can emit without closures. Lists and structs use the shim's dynamic variables with a
   64-bit node token (element index + 1 << 16 | member index + 1) and `RmlStructType<T>` member tables — one nesting
   level for now. The design's `Struct<T>()`/`Array<T>()` became `BindStruct`/`BindList` with explicit member tables.
3. **Lazy document loading.** `UiDocument` loads on first element access or before the next UI frame, so models created
   in `OnReady` exist before `data-model` binds — the design's example works as written; a model created later
   triggers a reload.
4. **`UiElement` events survive hot reload**: wrappers are cached by id and their native listeners re-attached after a
   reload; `Click` etc. are C# events, attached natively only while subscribed.
5. **Hot reload reads the sources**: Debug builds embed their project's `Content` path
   (`AssemblyMetadata("MainframeContentSource")`); the UI file interface checks those folders before the output copy
   and the watcher watches them, so edits apply without rebuilding. Release builds carry no paths.
6. **`SafeHandle`s never call RmlUi off-thread**: a handle finalized undisposed is queued and released by the UI server
   on the main thread; contexts and models destroyed by RmlUi itself are invalidated, not released twice.

## Consequences

- `UiElement.Click` (event) and `PerformClick()` (method) are distinct names.
- Nested data structures and `[UiBindable]` generation are follow-ups.
