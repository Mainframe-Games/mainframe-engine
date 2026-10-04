# ADR 0091 — Structured log entries, `ILogSink`s, zero-cost filtering

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M10 (lane m10b)

## Context

`Log` printed straight to the console. The editor's Output panel, the editor link and shipped games need the same
messages as data (level, time, category, call site), in files and in memory, and logging must stay free when a level
is filtered out — most call sites pass interpolated strings, which allocate before the call.

## Decision

- `LogEntry` (level, UTC timestamp, category, message, caller member/file/line) handed to every `ILogSink`; the sink
  list is copy-on-write (no lock while logging); a throwing sink is reported on stderr and skipped; a sink that logs
  does not recurse.
- The category comes from the existing `"[Category] message"` convention (interned, so steady logging of a category
  does not allocate it again); `Log.Write(level, category, message)` takes it explicitly. Existing call sites are
  unchanged.
- Per-level `[InterpolatedStringHandler]` overloads (`Log.Info($"...")`): the handler checks the level in its
  constructor, so filtered messages format nothing; formatting uses the invariant culture.
- Sinks: `ConsoleLogSink` (default, the classic format), `FileLogSink` (UTF-8, LF, previous runs kept as `.1`…,
  size rotation, flush per entry, in `UserDataPaths` — `MAINFRAME_USER_DATA` overrides), `MemoryLogSink` (ring with
  sequence-based `CopySince` for panels), `EditorLinkLogSink`.

## Consequences

- `GameHost.Run` adds a file sink per game; tools add a memory sink.
- Sinks run on the logging thread: slow sinks must queue. The file sink and the editor-link sink do (background writer threads, bounded queues, drops counted); review follow-up: a second process sharing a log folder writes `{name}-{pid}.log` instead of rotating the live file.
