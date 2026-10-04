# ADR 0012 — Source-generated type registry (`MainframeEngine.Generators`)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M2 (W2 lane A)

## Context

Scenes, the editor inspector (M10) and replication (M5) all need to enumerate a node type's serialized
properties and signals and get/set them by name, for engine and game types alike. Options: runtime
reflection (simple, slow, boxes, hostile to trimming/AOT) or compile-time generation. The plan approved
`Microsoft.CodeAnalysis.CSharp` only as a build-time dependency for writing a generator, and ruled out
`Microsoft.CodeAnalysis.Testing`.

## Decision

- A Roslyn **incremental** generator project, `MainframeEngine.Generators` (netstandard2.0, `IsRoslynComponent`,
  `EnforceExtendedAnalyzerRules`, `Microsoft.CodeAnalysis.CSharp` 4.14.0 with `PrivateAssets=all`), referenced
  as an analyzer (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`) by every project that declares
  node/resource types (engine, Sandbox, unit tests; games later; the NuGet package will carry it in
  `analyzers/`).
- For each class deriving from `Node` or `Resource` it emits a `NodeTypeInfo` with a factory, one
  `ExportPropertyInfo<TOwner,TValue>` per `[Export]` member (static lambdas; codec chosen at compile time), one
  `SignalInfo` per `[Signal]` event (typed add/remove + forwarder), migrations and traits, registered from a
  per-assembly `[ModuleInitializer]` into `TypeRegistry`. No `partial` requirement: members must be
  public/internal, which is checked (MFG001–MFG006, all errors).
- Models are value-equatable records (`EquatableArray<T>`, locations without syntax references) so the
  pipeline caches; an unrelated edit does not regenerate (tested).
- Reflection remains only where it is load-time and per-type: detecting which process callbacks a type
  overrides (cached), and binding named signal connections (`Delegate.CreateDelegate`).
- **Extension point for M5:** `TypeModel` carries one collection per member feature; `[Replicated]` adds
  its collection in `TypeModelBuilder` and its own emitter beside `RegistrationEmitter`.
- Generator tests drive it with `CSharpGeneratorDriver` directly (compile, emit, load in a collectible
  `AssemblyLoadContext`, assert behaviour and diagnostics); the unit-test project references the generator
  both as an analyzer and (aliased `generators`) as an assembly, plus `Microsoft.CodeAnalysis.CSharp`.

## Consequences

- No reflection or boxing on the serialization path; works for engine and game assemblies alike; ready for
  trimming except signal binding.
- Types the generator cannot see (private nested, generic) are not serializable; `TypeRegistry.GetNearest`
  falls back to the nearest registered base for signals.
- Adding a serializable value type means teaching the generator's codec table (`TypeModelBuilder.CodecFor`)
  and `Codecs` about it.
- The editor's code reload uses `TypeRegistry.UnregisterAssembly` + the reloaded assembly's initializer.
