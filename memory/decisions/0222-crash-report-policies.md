# ADR 0222 — Crash report policies: fail-fast nodes, both upload formats, shipped PDBs, logs on by default

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/crash-reporting.md

## Context

The G15 proposal left open how a throwing node behaves (Q3), which upload formats ship (Q4), whether player builds
carry PDBs (Q6), whether uploads include the log by default (Q7) and whether a Windows TDR re-creates the device (Q8).
Native capture (Q1, Q2, Q5) is ADR 0221.

## Decision

- **Q3: node exceptions stay fail-fast.** A callback that threw left its node half-updated, so the game reports and
  exits rather than logging and continuing (Godot's way). The scene tree only *notes* the node: an exception filter on
  each callback dispatch (process and physics process once per loop, the others per call) records the node path, type
  and callback in a weak table keyed by the exception and returns false, so nothing is caught and the stack is intact.
  No `crash.nodeErrors` option. The editor's Simulate (G7.5) keeps its own guard, as before.
- **Q4: both formats.** `"sentry-envelope"` (default; Sentry, self-hosted Sentry and compatible servers) and `"zip"`
  (a multipart POST of the bundle to the game's own endpoint). The docs point at self-hosted Sentry for teams that
  want their own receiver.
- **Q6: PDBs ship by default** next to the assemblies (better traces; the IL metadata exposes the names anyway). Every
  frame also carries the module MVID, the method token and the IL offset, so a build shipped without PDBs can be
  symbolicated offline with `mf-symbolicate`.
- **Q7: `includeLog` defaults to true**, with the privacy note in the docs and the Project Settings tooltip: games whose
  logs hold chat or player names turn it off. Reports are scrubbed of the home folder and user name either way, and
  nothing is sent without `crash.upload.url` and the player's agreement.
- **Q8: no device re-creation.** A `VK_ERROR_DEVICE_LOST` (a Windows TDR included) is a `DeviceLost` report and an
  exit; re-creating the device stays a possible later item.

## Consequences

- One report format (`report.json`, format 1) for desktop now and mobile (M13) later; uploaders are serializers, not
  SDKs, and live in the crash service (the only crash code that opens a connection).
- A game that wants Godot-style resilience must catch inside its own callbacks.
