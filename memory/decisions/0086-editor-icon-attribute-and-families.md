# ADR 0086 — `[EditorIcon]` in the engine core, families, generator-recorded doc summaries

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10c-icons)
- **Spec:** docs/design/editor.md#icons, docs/design/scene-serialization.md#source-generator

## Decisions

1. **`EditorIconAttribute` lives in the engine core** (namespace `MainframeEngine`), so game assemblies — which never
   reference the editor — can annotate their node and resource types and exported members. `Family` is an
   `EditorIconFamily` (Inherit, Logic, Space3D, Space2D, Ui, Audio, Physics, Network, Resource). Members also take
   `[Export(Icon = "…")]`.
2. **The generator records only what a type declares** (`NodeTypeInfo.Icon`, `IconFamily`; `ExportHints.Icon`). The
   editor's `EditorIcons` resolves inheritance (nearest base with an icon; nearest base with a family; resources default
   to `package`/Resource, everything else to `circle-dot`/Logic), so a game type without an attribute inherits its
   engine base's icon and an engine change of an icon needs no game rebuild. Types compiled without the generator
   (editor tools) fall back to reflection on the attribute.
3. **Families are set at category roots** (Node → Logic, Node3D → 3D, Node2D → 2D, the audio nodes → Audio, collision
   objects and shapes → Physics, UiLayer/UiDocument → UI, NetworkNode → Network, Resource → Resource), so e.g.
   AudioPlayer3D (a Node3D) is teal, not red. WorldEnvironment is 3D. Resources are one neutral family.
4. **Colours** (Godot hues lightened for the `#1f2128` panels): 3D `#fc7f7f`, 2D `#8da5f3`, UI `#8eef97`, audio `#5eead4`,
   physics `#fbbf24`, net `#c4a5ff`, logic `#a3abbd`, resource `#e2e6ee`.
5. **Doc summaries become editor text**: the generator stores the XML `<summary>` of types (`NodeTypeInfo.Description`,
   the create dialog) and exported members (`ExportHints.Description`, inspector tooltips) as plain text. It reads the
   `///` trivia itself because `GetDocumentationCommentXml` is empty unless the project generates a documentation file.
6. **Caching**: `EditorIcons` caches per type in a `ConditionalWeakTable` (collectible game types must not be pinned) and
   clears it on `TypeRegistry.Changed`; `Classes(type)` returns the same string instance, so data bindings stay
   allocation-free.
