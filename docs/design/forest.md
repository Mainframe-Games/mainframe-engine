# Forest

## Purpose

[`Examples/Forest`](../../Examples/Forest/) ("Mainframe Forest") is the engine's showcase: a first-person walk through a
small forest with a stream, meant to look like a UE or Unity scene (proposal
[future/forest-showcase.md](future/forest-showcase.md), G8d; [ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md)).
Like the [Demo](demo.md) it is a real game project built against this checkout (`MainframeEnginePath = ../..`) through
its own `Examples/Forest/Forest.slnx`, and it is **not** in `MainframeEngine.slnx`.

**Current state: the scaffold, the art and the audio.** The project, the first-person controller, a flat test scene
with a `River3D` stream, the CC0 art ([Assets](#assets): terrain layers, props, a sky panorama, the `ForestAssets`
manifest and an asset gallery scene) and the procedural [audio](#audio) (attached at run time by `ForestDev`) exist.
The valley (terrain), trees, grass, scattered props, the pause menu, the reference shots, the benchmark and the release
job come with the later waves of the forest slice.

## Layout

```
Examples/Forest/
├── Forest.slnx, Directory.Build.props, global.json, .gitignore, .gitattributes (the Demo's + *.hdr, *.exr, *.cube),
│   NOTICE.md (every third-party asset), README.md
├── project.mfproj        "Mainframe Forest": main scene forest, 1600 × 900, shadows High, the input map, autoload Dev
├── Forest/               Src/Player (FirstPersonController, IFootstepSurface + SurfaceBody3D, ForestSettings),
│                         Src/World (ForestScene, SceneWriter, ForestAssets, ForestAssetGallery), Src/Dev (ForestDev),
│                         Src/Audio (ForestAudio, …)
├── Forest.Desktop/       GameHost.Run(args, typeof(Forest.FirstPersonController).Assembly) + --write-scenes <dir>
├── Forest.Tests/         xUnit v3, no GPU; the asset tests skip without the LFS content
├── Tools/fetch_assets.py downloads and imports Content/Art (python3 + Pillow, curl)
└── Content/              Scenes/forest.mscene, Scenes/asset_gallery.mscene (generated), Settings/AudioBusLayout.mres
                          (the Forest's buses), Art/ (CC0, in LFS: Terrain/<Layer>/, Props/<id>/, Sky/)
```

## Assets

Brogan's choice for the art is "CC0 + procedural": trees, grass and the terrain's shape are generated (Ez Tree's bark and
leaf textures are already in the engine), and only surfaces and props are photographed assets, from
[ambientCG](https://ambientcg.com) and [Poly Haven](https://polyhaven.com) and nowhere else. Both sites are CC0 1.0
site-wide (their licence pages; neither API has a per-asset licence field). [NOTICE.md](../../Examples/Forest/NOTICE.md)
lists every asset (name, ID, source page and download URL, author, licence); THIRD_PARTY_NOTICES.md has a summary row.

**Import.** `python3 Examples/Forest/Tools/fetch_assets.py [--cache DIR]` (curl + Pillow) downloads over HTTPS from
`ambientcg.com/api/v2/full_json` + `ambientcg.com/get?file=…` and `api.polyhaven.com` + `dl.polyhaven.org` (Poly Haven
files checked against the API's MD5), converts, and writes `.meta` sidecars (UIDs) for new files; re-running keeps the
UIDs. Everything under `Content/Art` is in LFS except the `.gltf` JSON and the sidecars: **107 MB** in 77 LFS files
(terrain 31.5 MB, props 73.6 MB, sky 2.4 MB).

| Kind | Source → committed |
|---|---|
| Terrain layers | ambientCG 2K JPG sets → 1024² (the splat material packs every layer at `LayerTextureSize` 1024): `<Id>_Color.jpg`, `<Id>_NormalGL.jpg`, `<Id>_ORM.png` (R = AmbientOcclusion, G = Roughness, B = 0), `<Id>_Height.png` (8-bit, from Displacement) |
| Props | Poly Haven glTF + `.bin` + textures at 1K/2K; the grey roughness image replaced by the asset's ARM map (R = AO, G = roughness, B = metallic: glTF's packing) and wired as occlusion + metallic-roughness; the fern's diffuse JPG and Alpha map merged into an RGBA PNG |
| Sky | Poly Haven `lilienstein` (a sunlit meadow at a forest edge, low sun) tonemapped JPG → 4096 × 2048, 2.4 MB (the panorama path is LDR) |

| Layer (splat channel) | Tag | ambientCG | Tiling | Contrast |
|---|---|---|---|---|
| 0 Grass | `grass` | Ground037 (mossy forest grass; tinted (196, 212, 170): the photo is a bright yellow-green) | 2.5 m | 0.25 |
| 1 Leaves | `leaves` | Ground023 (leaf litter, sticks, dark soil) | 2.5 m | 0.25 |
| 2 Moss | `moss` | Moss002 | 2 m | 0.3 |
| 3 Rock | `rock` | Rock063 (mossy layered cliff) | 6 m | 0.2 |
| 4 Dirt | `dirt` | Ground067 (brown forest dirt) | 2.5 m | 0.15 |
| 5 Gravel | `gravel` | Ground108 (riverbed) | 1.5 m | 0.1 |
| 6 Mud | `mud` | Ground051 (dark wet mud, pebbles) | 2 m | 0.15 |
| 7 Needles | `needles` | Ground082S (soil under needles and twigs) | 2 m | 0.25 |

| Prop | Poly Haven | Size (m, y up) | Notes |
|---|---|---|---|
| `MossyRocks01`, `MossyRocks02` | rock_moss_set_01/02 (2K) | 8.0 × 1.8 × 7.0, 8.3 × 1.4 × 3.4 | six / seven mossy rocks each (one mesh node per rock) |
| `Boulder` | boulder_01 (2K) | 1.3 × 1.0 × 1.8 | 66 k triangles |
| `Rock07`, `Rock09` | rock_07, rock_09 (1K) | 0.17 × 0.14 × 0.32, 0.07 × 0.03 × 0.14 | stones: scale up (× 4, × 8 in the gallery) |
| `DeadTrunk`, `DeadTrunk02` | dead_tree_trunk, dead_tree_trunk_02 (2K) | 3.1 × 0.3 × 0.3, 4.1 × 1.1 × 1.1 | logs; 102 k and 83 k triangles |
| `Stump01`, `Stump02` | tree_stump_01 (2K), tree_stump_02 (1K) | 1.4 × 0.6 × 1.6, 1.5 × 0.5 × 1.4 | with roots |
| `DryBranches` | dry_branches_medium_01 (1K) | 1.1 × 0.3 × 1.3 | three branches |
| `Fern` | fern_02 (2K) | 2.0 × 0.4 × 1.7 | four clumps, alpha-tested (scale 1.2–1.5 reads as forest ferns) |

**`ForestAssets`** (`Forest/Src/World/ForestAssets.cs`): project-relative paths (`Content/Art/…`; `Resolve` =
`ContentPaths.Resolve`), descriptors and factories. Textures load through `ResourceLoader`, so they are shared and a
saved scene references them by UID; every factory call returns new resources, so keep what you build many times.

- `ForestLayerAsset` (`Name`, `Tag`, `AssetId`, `TilingMeters`, `HeightBlendContrast`, `Tint`, the four paths) for
  `Grass` … `Needles`, `TerrainLayers` in splat order, `LayerIndex(tag)`; `CreateTerrainLayer(layer)`,
  `CreateTerrainLayers()`, `CreateTerrainMaterial()` (a `TerrainSplatMaterial3D` with all eight).
- `ForestPropAsset` (`AssetId`, `Resolution`, `Kind`: Rock/Log/Stump/Debris/Plant, `Size`, `Cutout`, paths) for the
  eleven props, `Props`; `CreatePropMaterial(prop)`: PBR, metallic 0, roughness and occlusion from the ARM map, back
  faces culled for closed meshes (the glTFs ask for double-sided), cut-out and double-sided for the fern;
  `InstantiateProp(prop, material?)`: the glTF's nodes (a nested instance when saved) with the material as each mesh's
  `MaterialOverride` — the importer reads only Blinn-Phong colour and normal maps, so the override is what makes them
  PBR; `LoadPropMeshes(prop)`: each `ArrayMesh` with its transform, for `MultiMesh` scattering.
- `SkyPanorama`, `CreatePanoramaSky()`: an optional `Panoramic` sky (the physical sky stays the default).

**Asset gallery** (`ForestAssetGallery`, `Content/Scenes/asset_gallery.mscene`, written by `--write-scenes`): a 64 m
terrain with one 7 m band per layer west to east (the rock band is a steep ridge: triplanar), a leaves / needles / moss
mix in front, every prop on it, a shadowed sun and the panorama sky. Game arguments: `++ --view overview|terrain|props`,
`--sky panorama|physical`:

```
dotnet Examples/Forest/Forest.Desktop/bin/Release/net10.0/Forest.Desktop.dll --scene Content/Scenes/asset_gallery.mscene \
  --fixed-fps 60 --max-frames 120 --screenshot gallery.png ++ --view props
```

![The asset gallery, props view](../images/forest/asset-gallery.jpg)

Checked in the gallery: albedo in sRGB (the props and bands match their photos), OpenGL normals lit from the sun's
side, ORM roughness (gravel and mud read rough, a few rock faces glossy as scanned), the fern's alpha test and the scale
of each prop against the 7 m bands.

## The test scene

`ForestScene.Build()` writes `Content/Scenes/forest.mscene` (`dotnet run --project Examples/Forest/Forest.Desktop --
--write-scenes Examples/Forest/Content/Scenes`); `ProjectTests.CommittedSceneMatchesTheBuilder` checks the committed
file byte for byte, as the Demo does. It holds:

- a 256 m `SurfaceBody3D` ground (top at y = 0, surface `moss`) with a 6 m wide, 1.2 m deep trench along Z at
  x = 16 … 22 (floor `gravel`) and 6 m ramps out at both ends;
- the test stream: a `River3D` along the trench (66 m, surface −0.15 → −0.3 m, width 6 m, depth 1.05 → 0.9 m), so
  wading the trench slows the walk to ≈ 0.45 × and the flow pushes north;
- crates (`wood`, one stack to jump onto), 15°, 35° and 55° rock slopes (the last too steep), a 1.3 m beam to crouch
  under and a boulder;
- a shadowed `DirectionalLight3D` (3 cascades) and a `WorldEnvironment` with the procedural sky, its sun along the
  light;
- the player at (6, 0, 8) facing north-east towards the stream.

The content wave replaces it with the valley (`ValleyGenerator`, `--write-terrain`), authored in the editor.

## `FirstPersonController`

`Forest/Src/Player/FirstPersonController.cs`, a `CharacterBody3D` whose origin is at the **feet**: a capsule `Shape`
(`Radius` 0.3 m, `StandingHeight` 1.75 m), a `Head` at eye height (top − 0.12 m) and `Head/Camera`. Children the scene
does not have are created unowned when it is ready.

- **Look.** Mouse motion (`InputEventMouseMotion.Relative` × `MouseSensitivity` 0.1°/count, `InvertY`) per event in
  `OnInput` while the mouse is captured (`Input.MouseMode`, ADR 0125); the right stick (`look_*`) in `OnProcess` with a
  0.15 radial deadzone and a squared curve at `GamepadLookSpeed` 140°/s; pitch within ±89°. **Yaw and pitch both live on
  the head**: the body's transform is physics-interpolated (ADR 0024), so a yaw set on it between steps would be
  overwritten by the render pose. `AddLook(yaw, pitch)` turns it from code.
- **Mouse capture.** Captured when ready (`CaptureMouse`; `++ --no-capture` turns it off), released by `pause`
  (Escape / Start) and recaptured by a click. The pause menu comes later.
- **Move.** `move_*` in the head's yaw frame; walk 2.5, sprint 5 (`sprint` held or `sprint_toggle`, which clears when
  the stick is released), crouch 1.3 m/s (`crouch` held or `crouch_toggle`); `Acceleration` 14 m/s² on the floor,
  `AirControl` 0.3. The controller keeps its **own** horizontal velocity and, on the floor, moves along the floor plane
  at that speed (Godot's `floor_constant_speed`): reading the speed back from `Velocity` would lose the part
  `MoveAndSlide` removes into a slope on every uphill step (0.7 m/s up a 30° ramp). Walls remove the intent going into
  them. `FloorMaxAngle` stays 45°, `FloorSnapLength` 0.3 m in the scene.
- **Jump.** `JumpVelocity` 4.2 m/s (≈ 0.9 m), `Gravity` 9.81, `CoyoteTime` 0.1 s, which is also the jump buffer.
- **Crouch.** The capsule becomes `CrouchHeight` 1.1 m (bottom fixed); the head eases over 0.15 s. Standing up first
  checks a slightly thinner standing capsule with `DirectSpaceState.IntersectShape` (preallocated list); if blocked it
  stays crouched.
- **FOV.** `HorizontalFov` 90° (60–110) sets the camera's vertical FOV from the viewport's aspect
  (`VerticalFov(h, aspect)`).
- **Head bob.** From the walked distance: vertical at the stride frequency (lowest at each footstep), sideways at half,
  `HeadBobAmount` 0.035 m scaled by speed and eased out when stopping; `HeadBob` switches it off.
- **Footsteps.** `[Signal] Footstep(string surface)` once per `StrideLength` (0.75 m) on the floor and on landing. The
  surface is `"water"` when `WaterDepthAt(feet) ≥ 0.05 m`, else the floor collider's `IFootstepSurface` (a ray down from
  the feet; `SurfaceBody3D` has an exported `Surface`), else `SurfaceResolver` (a `Func<Vector3, string?>`; the content
  wave sets it to `Terrain3D.SurfaceAt`'s layer name), else `DefaultSurface` ("default"). `FootstepAudio` plays them
  ([Audio](#audio)).
- **Wading.** `World3D.Water` ([Water](water.md#water-queries)): speed × `WadeSpeedScale(ImmersionAt(feet))`, plus the
  flow × `FlowPush` 0.3; more than `MaxWadeImmersion` 1.2 m ahead is a soft wall (no swimming).
- **Settings.** `ForestSettings` (FOV, sensitivity, invert Y, pad look speed, head bob) is read from `settings.json` in
  the user data folder when the game runs under `GameHost`, until G4's `UserSettings` exists.
- Nothing allocates per frame.

### Input map

`project.mfproj`: `move_forward/back/left/right` (WASD, arrows, left stick, deadzone 0.2), `look_left/right/up/down`
(right stick, deadzone 0: the controller applies its radial deadzone), `sprint` (Shift), `sprint_toggle` (left stick
press), `crouch` (C, Ctrl), `crouch_toggle` (B), `jump` (Space, A), `pause` (Escape, Start), `photo` (F2, Back; for the
HUD later).

## Audio

`Forest/Src/Audio/`: the whole soundscape is **procedural** (no recorded or downloaded audio, so nothing to list in
`NOTICE.md`): seeded noise, filters and ZzFX, synthesised once into in-memory `AudioStream`s (`AudioStream.FromSamples`;
[ADR 0162](../../memory/decisions/0162-procedural-forest-audio.md)).

### Wiring

```csharp
var audio = ForestAudio.Attach(sceneRoot, river, player, fallPositions: [new Vector3(…)]);   // or a ForestAudio node in the scene
audio.PineDensity = p => PineDensityAt(p);   // optional (0–1): quieter songbirds, more woodpecker in the pines
```

`ForestAudio` (a `Node`; exports `RiverPath`, `PlayerPath`, `FallPositions`) creates its parts when ready, unowned
(never saved): `Ambience`, `Stream`, `Birds` under itself and `FootstepAudio` under the player. With empty paths it finds
the first `River3D` and `FirstPersonController` in its scene (its `Owner`, else its parent). It applies the volumes in
`ForestSettings.Audio` (`settings.json`) when the game runs under `GameHost`.

### Parts

| Part | What | Bus |
|---|---|---|
| `AmbienceAudio` | two non-positional stereo loops: **wind roar** (24 s, 32 kHz: decorrelated noise per channel through a band-pass whose centre rises with the gusts, 180 → 700 Hz, plus a low rumble; the right channel lags 0.35 s so gusts sweep across) and **leaf rustle** (17 s: 2–8 kHz noise with a fast flutter under its own gusts squared). The gust envelopes are sums of sines at whole multiples of the loop frequency, so they repeat with the loop; the noise is folded into a seamless loop by an equal-power crossfade of a 1 s tail. `WorldEnvironment.WindStrength` (the trees' wind) sets the levels (calm: roar −6 dB, rustle −12 dB) and the roar's pitch (0.9–1.08), eased over 2 s | Ambience |
| `StreamAudio` | the **brook** (14 s mono: band-passed noise at 800 Hz and 2.5 kHz that undulates, a low rumble, ≈ 38 Minnaert bubbles per second — sines at 350 Hz – 2.4 kHz whose pitch rises 1.3–2.4× as they decay — and bursts of babble) on two `AudioPlayer3D`s: the near one at the river point closest to the listener (`Curve3D.GetClosestOffset`), pushed toward the listener by up to half the width; the far one 12 m further along, −5 dB, half a loop apart. Both move at most 30 m/s. Level `StreamVolumeDb + clamp(20·log10(flow / 1 m/s), −6, +6)` from `River3D.FlowSpeedAt`; `UnitSize` 3 m, `MaxDistance` 30 m, low-pass to 2.5 kHz at range. The **falls** loop (10 s: the brook's recipe, brighter and denser) plays at each of `FallPositions` (`MaxDistance` 80 m); `River3D` has no falls yet, so the positions are parameters, and `StreamAudio.FindSteepPoints(river, minSlope 0.25, spacing 15 m)` finds candidates | Water |
| `BirdSongs` | a pool of 6 `AudioPlayer3D`s (`UnitSize` 8 m, low-pass at range); Poisson-timed (mean 4 s, at least 0.8 s apart), 10–40 m away and 6–16 m above the listener; a species never twice in a row, nor a variant. Five ZzFX species (`BirdSynth`): Warbler (sliding whistles), Chickadee ("fee-bee"), Finch (a trill from ZzFX's repeat + tremolo, then a flourish), Thrush (flute phrases with pitch jumps, a soft trill), Woodpecker (a drum roll of `tan` knocks, slowing). Four songs and two calls each; pitch randomness 0.04. With `PineDensity`, songbirds are up to 8 dB quieter in dense pines and the woodpecker is likelier (weight 0.35 → 2) | Ambience |
| `FootstepAudio` | `FirstPersonController.Footstep` → one of 6 variations of the surface's step, never the same twice in a row, `PitchRandomness` 0.08, ±1.5 dB, crouch −6 dB, sprint +2 dB; two alternating players at the feet. Each step is a heel and a softer toe impact 50–90 ms apart built from thumps, filtered noise bursts, grains (crunch), resonant modes and sweeps: `grass`, `leaves` (also `leaf_litter`), `moss`, `rock`, `dirt` (also `path`), `gravel`, `mud`, `water` (a splash with droplets), `wood` (the log bridge) and `default` | Foley |

`ForestSoundBank.Shared` builds each group on first use (≈ 0.5 s in Debug for all of it, at scene load; about 17 MB of
samples, mostly the stereo beds); `Preload()` builds everything now. All synthesis is deterministic for its seed
(`ForestRandom`, SplitMix64).

### Mix

`Content/Settings/AudioBusLayout.mres` (`project.mfproj` → `audio.busLayout`): Master (16 voices) → **Ambience** −7 dB
(10), **Water** −8 dB (6), **Foley** −7 dB (6, a light Freeverb: room 0.3, damp 0.7, wet 0.08), Music, SFX, UI. Measured
through the offline path (`ForestAudioRenderTests`, master mix):

| Scene | RMS | Peak |
|---|---|---|
| bed, calm / full wind | −42 / −35 dBFS | −25 / −19 dBFS |
| brook 4 m from the bank / 25 m | −32 / −47 dBFS | −13 / −29 dBFS |
| falls at 8 m | −30 dBFS | −14 dBFS |
| birds (a song every 2 s) | −40 dBFS | −20 dBFS |
| footsteps at a walk | −36 … −28 dBFS | −16 (moss) … −10 (wood) dBFS |
| everything, walking by the stream | −26 dBFS | −9 dBFS |

`ForestAudioSettings` (`MasterVolume`, `AmbienceVolume`, `WaterVolume`, `FoleyVolume`, linear 0–1, default 1) scale the
buses over the layout's levels (`ForestAudio.ApplySettings`; 0 mutes).

### Per frame

Struct maths and property sets on existing players: the stream's closest-point search over the baked curve, the bird
schedule and footsteps allocate nothing (`ForestAudioTests.FramesAllocateNothing`: 600 frames of walking with birds,
footsteps on every surface and the server rendering, 0 B).

## `ForestDev` and `--autowalk`

The `Dev` autoload (`ForestDev`) is idle unless started with game arguments after `++`:

- `--autowalk` presses the move actions along `ForestDev.AutoWalkPath` (walking, turning, sprinting, a jump, wading up the
  stream and out over the ramp; no crouch, which rebuilds the capsule), then measures a 600-frame window after 180
  warm-up frames with `GC.GetAllocatedBytesForCurrentThread` on the main thread (the whole frame: tree, physics,
  rendering, UI) and quits with exit code 0 when it allocated nothing, 1 otherwise (an `[ERROR]` line). With
  `--fixed-fps 60` the walk is the same on every run. Measured: 0 B, 21 footsteps, 0 gen-0 collections.
- `--no-capture` leaves the mouse free.
- When the current scene has no `ForestAudio`, `ForestDev` attaches one on its first frame (the scene's river and
  player), so the test scene has sound; `--no-audio` skips it. Measured: `--autowalk` with the audio still allocates
  0 B (headless, null device).

## Recipes

| Recipe | What |
|---|---|
| `just forest *args` | `dotnet run --project Examples/Forest/Forest.Desktop -c Release -- {{args}}` |
| `just forest-test` | `dotnet test Examples/Forest/Forest.Tests` |
| `just forest-audio` | the offline audio renders: `Examples/Forest/artifacts/audio/*.wav` (ambience calm and windy, brook near and far, falls, birds, footsteps per surface, the whole mix walking) |
| `just forest-screenshots [frames]` | `build/forest-screenshots.sh`: one Release shot of the main scene into `docs/images/forest/forest.png` (the five reference shots come later) |
| `just forest-bench` | prints "not yet" (G8d.15) |

## Tests

`Forest.Tests` (`ControllerHarness`: a headless tree with single-threaded physics and the project's input map at
60 Hz):

- walk, sprint and crouch speeds, acceleration, the yaw frame; jump height ≈ v²/2g, coyote time, the jump buffer;
- a 30° slope climbs, a 55° one does not; standing up waits for headroom; the crouch toggle;
- wading speed, the deep-water wall, the flow push from a `River3D`;
- mouse look only while captured, sensitivity, invert Y and the pitch clamp; pause releases the mouse and a click
  recaptures it; the gamepad deadzone and curve; the FOV conversion;
- head bob amplitude and switch; one footstep per stride with the floor's surface; the resolver and default fallbacks;
- 600 physics ticks of walking, sprinting, jumping and turning in water allocate 0 B;
- settings round trip; the project file (main scene, keyboard and pad bindings); the scene contract; the stream is
  wadeable in the trench; `--autowalk` drives the actions;
- `ForestAssetsTests`: the layer tags and order; every manifest file exists, and every file under `Content/Art` is in
  the manifest with a `.meta`; NOTICE.md names every asset; the layers load 1024² textures with resource paths; the ORM
  maps have no metal; every prop imports through Assimp with one shared PBR override whose ORM is the ARM map (the
  fern's albedo has alpha) at its documented size; the sky is a 4096 × 2048 JPG under 10 MB; the gallery scene matches
  its builder and paints one layer per band. The LFS-dependent tests skip when the art is only pointers.
- audio (`ForestSynthTests`, `ForestAudioTests`, `ForestAudioRenderTests` over `AudioHarness`: the controller harness
  plus an `AudioServer` on the manual null device with the Forest's bus layout): every sound is deterministic per seed,
  loops are seamless, levels and frequency bands are where they should be, birds sing in their register; the wiring, the
  wind following the environment, the brook's emitters (nearest point, half-width push, 30 m/s glide, louder with
  flow), steep-point detection, the Poisson bird schedule (count, spacing, no repeats, determinism, pines), footsteps
  per surface without repeats and from the controller's signal, settings on the buses, 0 B per frame; and the offline
  renders above, which must not clip and must stay within their RMS and peak windows.

Not in CI yet (the G8d.9 CI step and `build/forest-smoke.sh` come with the release work).

## Related

[Water](water.md) · [Audio](audio.md) · [Demo](demo.md) · [Cameras & input](cameras-and-input.md) · [Physics](physics.md) ·
[Projects & GameHost](project-and-gamehost.md) · [future/forest-showcase.md](future/forest-showcase.md)
