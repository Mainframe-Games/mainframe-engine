# ADR 0283 — The gameplay toolkit is split into small milestones, and rows can be skipped

- **Date:** 2026-11-15
- **Status:** accepted
- **Docs:** docs/milestones.md, memory/context/current-state.md

## Context

The gameplay toolkit was one milestone holding 43 rows from G1a to G20.3: animation, 2D, navigation, saves, export,
rendering, world building, showcases, physics, UI, video and more. It sat at ⬜ while most of its rows were done, so its
status said nothing, and its intro paragraph mixed the ordering notes of a dozen unrelated features. The owner found it
hard to track and wanted to drop the parts the games will not need without deleting their proposals.

## Decision

- The toolkit becomes 13 milestones with their own sections and overview rows: 3D animation, 2D sprites and tile maps,
  Navigation, Shipping games, Rendering, Developer tools, World building, Showcases, Physics and paths, Player UI, input
  and accessibility, Video playback, 2D skeletal animation, Video editor. Each section holds its own ordering notes.
- Rows keep their G numbers. Code, ADRs, logs and proposals cite G1c or G8e.3, and renumbering would break all of
  them. "Gameplay toolkit" stays as the name of the family (the roadmap's subgraph and the release-notes area).
- A new status, ⏭ skipped: the owner marks a row, or a whole milestone, as not needed, with the date and the reason. Its
  proposal stays, and un-skipping is flipping it back to ⬜. A milestone is ✅ when each row is ✅ or ⏭.
- The split landed in a state sync, which applied the pending G1c entries in the same PR. The only other edits to
  `docs/milestones.md` or the state file were wording changes the split forced, on the owner's request (ADR 0280
  limits a sync to pending entries otherwise).

## Ruled out

- **Renumbering the milestones (G1–G13 or new letters).** It would break every existing reference.
- **One milestone per G number.** About 20 sections, many of them with a single done row. Grouping by what a game needs
  keeps each milestone skippable as one piece.
- **Deleting rows that are not needed.** It loses the proposal link and the record that the feature was considered.

## Consequences

- Each milestone's status shows what is open at a glance; most of the toolkit's milestones are ✅.
- A new G feature goes into the milestone it belongs to, or into a new one, with its next G number.
- Links to `milestones.md#gameplay-toolkit-` now point to the new section anchors.
