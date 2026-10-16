# MyGame

A game made with [Mainframe Engine](https://github.com/Mainframe-Games/mainframe-engine).

- `MyGame/`: your nodes and resources (C#). `MyGame.Desktop/`: the executable (`GameHost.Run`).
- `project.mfproj`: the project settings (main scene, window, input, splash, …). `Content/`: scenes, UI, art.

Run it with `dotnet run --project MyGame.Desktop` (add `-- --no-splash` while you develop), or open this folder in the
Mainframe editor.

## What your game inherits

Mainframe Engine is MIT. It is built on [BRUTAL](https://github.com/ahwootech/BRUTAL) (Ahwoo Limited), whose licence
(BRUTAL License v1.0) adds three conditions to every product that includes it. The engine meets all three for you:

1. **Credits**: "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited." is on the Credits tab of the
   engine's settings menu (`SettingsMenu`). If you build your own credits screen, list `Credits.Entries`.
2. **Logo before gameplay**: the splash sequence (`"splash"` in `project.mfproj`) shows the BRUTAL logo after the Mainframe
   logo every time the game starts. It cannot be removed unless the game is non-profit (`"nonProfit": true`; confirm
   with Ahwoo at brutal@ahwoo.com) or you have Ahwoo's written permission. Add your studio's card after it.
3. **NOTICE**: every build and publish copies `BRUTAL-NOTICE.txt` next to the game executable. Ship it.

A dedicated server (`--headless`) displays nothing and shows no splash. The engine's `THIRD_PARTY_NOTICES.md` lists
everything else it includes.
