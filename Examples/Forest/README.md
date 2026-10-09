# Mainframe Forest

A first-person walk through a small forest with a stream: the engine's showcase scene
([docs/design/forest.md](../../docs/design/forest.md), proposal
[docs/design/future/forest-showcase.md](../../docs/design/future/forest-showcase.md)). It is a game project of its own,
built against this checkout's engine, and **not** part of `MainframeEngine.slnx`.

**Status: scaffold.** The scene is a flat 256 m test ground with a trench holding a test `River3D` stream, boxes, slopes
and a low beam. The generated valley, trees, terrain, audio and the reference shots come with the content wave.

## Run

```bash
just forest                                  # Release; Esc releases the mouse, a click recaptures it
just forest --fixed-fps 60 ++ --autowalk     # walk a fixed loop and check a 600-frame window allocates 0 B
just forest-test                             # Forest.Tests: controller, project file, scene contract (no GPU)
just forest-screenshots                      # docs/images/forest (display awake)
```

or `dotnet run --project Examples/Forest/Forest.Desktop -c Release`. Game flags go after `++`: `--autowalk`,
`--no-capture` (do not capture the mouse).

## Controls

| Action | Keyboard and mouse | Gamepad |
|---|---|---|
| Move | W A S D (or arrows) | left stick |
| Look | mouse | right stick |
| Sprint | Shift (held) | left stick press (toggle) |
| Crouch | C or Ctrl (held) | B / Circle (toggle) |
| Jump | Space | A / Cross |
| Release the mouse | Escape (click to recapture) | Start |

Settings (FOV, mouse sensitivity, invert Y, pad look speed, head bob) are read from `settings.json` in the game's user
data folder when present.

## Layout

```
Examples/Forest/
├── Forest.slnx, Directory.Build.props, global.json, .gitignore, .gitattributes, NOTICE.md
├── project.mfproj        "Mainframe Forest": main scene, 1600 × 900 window, input map, autoload Dev
├── Forest/               node library: Src/Player (FirstPersonController, footstep surfaces, settings),
│                         Src/World (the scene builder), Src/Dev (ForestDev: --autowalk)
├── Forest.Desktop/       GameHost.Run(args, …) + --write-scenes <dir>
├── Forest.Tests/         xUnit v3; no GPU and no LFS content
└── Content/              Scenes/forest.mscene (generated), Settings/AudioBusLayout.mres
```

Regenerate the scene after changing `ForestScene`:

```bash
dotnet run --project Examples/Forest/Forest.Desktop -- --write-scenes Examples/Forest/Content/Scenes
```
