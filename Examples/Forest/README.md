# Mainframe Forest

A first-person walk through a small forest with a stream: the engine's showcase scene
([docs/design/forest.md](../../docs/design/forest.md), proposal
[docs/design/future/forest-showcase.md](../../docs/design/future/forest-showcase.md)). It is a game project of its own,
built against this checkout's engine, and **not** part of `MainframeEngine.slnx`.

**Status: playable.** A generated 256 m valley: a stream from a rocky outcrop over a small fall, through three pools
and under a fallen-log bridge into a pond; an oak and ash glade, aspen banks, a dense pine slope; a ≈ 3 minute walking
loop from the trailhead. Physical morning sky, valley fog, light shafts, eye adaptation, TAA, procedural audio. Not yet:
the pause menu, impostors, SSAO and the release build.

![The glade](../../docs/images/forest/r1-glade.png)

## Run

```bash
just forest                                  # Release; Esc releases the mouse, a click recaptures it
just forest --fixed-fps 60 ++ --autowalk     # walk the path and check a 600-frame window allocates 0 B
just forest --fixed-fps 60 ++ --autowalk-lap # walk the whole loop back to the trailhead
just forest ++ --shot 3                      # a fixed camera at reference shot R3 (1–5)
just forest-test                             # Forest.Tests: controller, valley generator, project file (no GPU)
just forest-screenshots                      # the reference shots R1–R5 into docs/images/forest (display awake)
just forest-bench                            # fly the benchmark spline at 1920 × 1080: p50/p90/p99, JSON, baseline
```

or `dotnet run --project Examples/Forest/Forest.Desktop -c Release`. Game flags go after `++`: `--autowalk`,
`--autowalk-lap`, `--shot <n>`, `--view x,y,z,tx,ty,tz`, `--benchmark`, `--resolution WxH`, `--set Node.Property=value`
(tune the look for a run), `--no-capture` (do not capture the mouse), `--no-audio` (no procedural soundscape;
`just forest-audio` renders it to WAV files).

The art (CC0, from ambientCG and Poly Haven, listed in [NOTICE.md](NOTICE.md)) is in `Content/Art`, stored with Git LFS:
run `git lfs pull` once. `just forest --scene Content/Scenes/asset_gallery.mscene ++ --view props` (or `terrain`,
`overview`) shows every terrain layer and prop; `python3 Tools/fetch_assets.py` re-imports them.

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
├── project.mfproj        "Mainframe Forest": main scene, 1920 × 1080 px window, TAA, input map, autoload Dev
├── benchmark-baseline.json  just forest-bench's reference (this Mac)
├── Forest/               node library: Src/Player (FirstPersonController, footstep surfaces, settings),
│                         Src/World (the scene, ForestValley + ValleyGenerator + ForestVegetation, ForestAssets, the
│                         asset gallery), Src/Dev (ForestDev, PathWalker, ForestBenchmark), Src/Audio
├── Forest.Desktop/       GameHost.Run(args, …) + --write-scenes <dir>
├── Forest.Tests/         xUnit v3; no GPU (the asset tests skip without the LFS content)
├── Tools/                fetch_assets.py: downloads and imports Content/Art
└── Content/              Scenes/forest.mscene, Scenes/asset_gallery.mscene (generated), Settings/AudioBusLayout.mres,
                          Art/ (CC0 terrain layers, props, sky)
```

Regenerate the scene after changing `ForestScene`:

```bash
dotnet run --project Examples/Forest/Forest.Desktop -- --write-scenes Examples/Forest/Content/Scenes
```
