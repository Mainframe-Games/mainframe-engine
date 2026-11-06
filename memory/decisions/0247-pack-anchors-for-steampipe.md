# ADR 0247 — Pack entries start pages at path-hash anchors, so Steam patches stay small

- **Date:** 2026-11-06
- **Status:** accepted
- **Milestone:** G19 (phase 2)
- **Docs:** docs/design/asset-pipeline.md#packs, docs/design/future/protected-exports.md, [ADR 0239](0239-protected-exports.md)

## Context

Exports ship one `Assets.mfpak`. SteamPipe splits files into roughly 1 MB chunks and downloads only the chunks that
changed. Valve's uploading guide recommends a 1 MB padding alignment for pack files, per-asset compression, and one
table of contents: data that moves by anything other than whole chunks becomes new chunks.

A pack laid out back to back moves every later byte when one asset changes size. Padding entries so none crosses a
1 MB page is not enough either: greedy packing re-flows every later page after an insert (the first version of
`InsertingAnAssetShiftsLaterEntriesByWholePages` showed it).

## Decision

- An entry of a page (1 MB) or more always starts on a page boundary.
- A smaller entry starts a page when the first 4 bytes of SHA-256 of its path, as a fraction of 2³², fall below
  `storedLength / 16 MB` (`PackFormat.IsAnchor`). That is about one anchor per 16 MB of entries. The choice depends
  only on the entry itself, never on what came before.
- Other entries follow at 16 KB (stored) or 16 bytes (Brotli) alignment.
- Compression is per entry (Brotli for text and data, media stored), and the index sits at the end.

A change then re-lays out only the entries between it and the next anchor. Everything after the anchor moves by whole
pages, so SteamPipe still matches those chunks.

## Ruled out

- **One page per entry:** a project with thousands of small files would grow by gigabytes on disk.
- **A stable, append-only layout that reuses the last pack's offsets:** builds would depend on history, so a clean CI
  export would differ from an incremental one. That breaks the determinism ADR 0239 and ADR 0240 rely on.
- **Content-defined chunking inside entries:** more code than the gain over entry-level anchors justifies.

## Consequences

- About 3 % padding (half a page per 16 MB) plus the padding before large entries. Steam compresses zeros on download.
- One change costs up to the anchor spacing (about 16 MB) of re-downloaded chunks in the worst case, usually far less.
- The human checklist's depot upload confirms the patch size SteamPipe reports.

## Revisit when

- The depot check shows patches much larger than the changed assets, or Valve changes its chunking.
