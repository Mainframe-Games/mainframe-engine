# ADR 0141 — Every log entry says whether the engine or the game wrote it

- **Date:** 2026-10-06
- **Status:** accepted (Brogan: "make it obvious what is an engine log and what is a game log")

## Context

The console, the log file and the editor's Output panel interleave the engine's lines (Vulkan, audio, UI, networking)
with the game's. Crash Site Defense had printed its own lines straight to stdout, so they reached neither the log file
nor Output; moving the game onto `Log` makes both kinds go through the same sinks, so the source has to be visible.

## Decision

1. `LogEntry.Source` (`LogSource.Engine` / `LogSource.Game`) comes from the entry's caller file, which every entry
   already carries: a file in one of the engine checkout's `MainframeEngine*` projects is the engine, anything else is
   the game. The engine's root is taken from the compiler's path of `LogSource.cs` itself, so it matches local paths and
   deterministic `/_/` paths alike. No registration, no API change for games, no cost beyond a prefix compare.
2. Line format (console and file): `[12:00:01.234] [INFO] engine Vulkan: …` and `[12:00:01.240] [INFO] game Net: …`
   (no `Name: ` when the entry has no category). The level keeps its colour on the console; the rest is plain.
3. The editor's Output panel tags a game process's entries `game·Net` (the game's code) or `engine·Vulkan` (the engine
   inside that process); every game entry shows the gamepad icon, engine entries their subsystem icon.

## Consequences

- Tools that read logs see the new prefix; the message text is unchanged (`grep "world running"` still matches).
- A game that copies engine source files into its own projects would see them tagged `game`; nothing does today.
