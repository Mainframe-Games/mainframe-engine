# ADR 0102 — Scene format 2: flat node list, stable resource keys, compact arrays

- **Date:** 2026-10-05
- **Status:** accepted
- **Amends:** [ADR 0011](0011-json-scenes-no-binary-bake.md) (the file layout; JSON and "no binary bake" stand)

## Context

ADR 0011's JSON layout was the default `Utf8JsonWriter` output, and three things made scene files diff and merge
worse than they need to:

1. Inline resources were keyed `"1"`, `"2"`… in discovery order. Adding a resource near the top of the tree
   renumbered every later key and every `{"res": "N"}` pointing at them, so a one-node edit diffed across the file
   and two branches adding resources almost always conflicted.
2. Children were nested inside their parents: reparenting re-indented a whole subtree (a delete plus an add in a
   diff), and deep scenes drifted right.
3. Arrays were one element per line (ADR 0011 chose this for component-level diffs): a `Vector3` took five lines, a
   colour six, a transform fourteen, and a moved node usually changes every component anyway.

Godot's `.tscn` solves 1 and 2 with random per-file sub-resource ids (`resource_scene_unique_id`) and a flat node
list with `parent` paths. Its `uid://` sidecar files are a separate mechanism (cross-file ids) and are not adopted:
this engine already keeps scene/resource UIDs inside the file and `.meta` sidecars only for imported binaries.

## Decision

Format 2 (`SceneFormat.Current = 2`), still JSON through `System.Text.Json`:

- **`"nodes"`**: a flat list in tree order, the root first. Every other entry has `"parent"`, its parent's path from
  the root (`"."` for the root's children). Nodes added inside a nested instance are ordinary entries whose parent
  path runs through the instance; the parser attaches them to the nearest listed instance (`NodeEntry.ParentPath`),
  so `PackedScene` instantiates both formats with the same code. Overrides stay on the instance entry.
- **Resource keys** are `Type_xxxxx` (five base-36 digits). An inline resource remembers the key it was loaded with
  (`Resource.SceneLocalId`, internal) and keeps it; a new resource's key is an FNV-1a hash of where it is first used
  (node path + property, or referencing key + property), so saves are deterministic (the Sandbox's
  `--write-scene` and code-built scenes produce identical bytes run to run) without storing anything new. External
  references are keyed by a hash of their UID. Collisions within a file are re-hashed with a counter.
- **Layout** (`SceneJsonLayout`, public as `SceneFormat.FormatJson`): two-space indent, `\n`, one member per line,
  but arrays of at most 16 scalars and `{ "res": "…" }` references on one line. The editor's `ReferenceFixer` uses it
  when it rewrites scenes and resources, so a move fix-up diff touches only the path hints.
- **Format 1** still loads (nested `"root"`; its numbered keys are not kept, so the next save re-keys the file).
  Committed scenes, the template and the example were re-saved as format 2.

## Consequences

- The Sandbox scene went from 856 to 570 lines (18 KB → 14 KB); a node's entry no longer depends on its depth.
- Adding, removing or reordering resources leaves the other keys and every reference to them unchanged.
- Reparenting still moves a node's block within the list (tree order) and changes its descendants' `parent` lines,
  as in Godot; the content lines themselves no longer change.
- Tools reading scene JSON handle both layouts: the editor's `FilePeek` (root type: first of `"nodes"`), the
  `mf-l10n` scene extractor.
- Hand-written format 2 files must name every non-root node's parent; the parser reports a missing parent, a parent
  that is not an earlier node (or inside an instance), duplicate paths and a root with a parent.
