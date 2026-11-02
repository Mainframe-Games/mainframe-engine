---
summary: >-
  A "Why Mainframe?" section in the README and on the landing page: the game is the engine, and an honest answer to
  "why not Godot?".
---

# 2026-11-03: Why Mainframe

Branch `feature/why-mainframe`. No plan (small, docs only).

## What changed

- README: "Why Mainframe?" after the editor screenshot, with "The game is the engine" (one C# process, the editor is a
  game too, Play runs the shipped executable, standard .NET tooling, nothing out of reach) and "Why not Godot?" (C#
  all the way down, Godot's model kept, the shipping kit built in, then the trade-offs: desktop only, young).
- Landing page: `WhyMainframe.astro` after the showcase, the same content as `Features`-style cards that link to the
  doc behind each claim; its rows are centred (`landing.css`). `docs-site.md` lists the section.

## Decisions

- Lead the Godot answer with "if Godot works for you, use it" and state the trade-offs: the pitch is for a specific
  audience (C# developers, Godot users, solo devs shipping on Steam), not "better than Godot".
- "The game is the engine" means there is no separate runtime: a game is a .NET program that contains the engine, and
  the editor is built the same way.

## Dead ends

- None.

## Verified

- `just docs-build` passes; all eight card links return 200; the section lays out at 1400 px and 375 px with no
  horizontal scroll. `check.sh` passes.

## Not verified

- The light theme, by eye.

## Next

- Keep the README section and `WhyMainframe.astro` in step when the platforms or the shipping kit change.
