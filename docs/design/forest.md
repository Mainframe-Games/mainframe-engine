# Forest

## Purpose

[`Examples/Forest`](../../Examples/Forest/) ("Mainframe Forest") is the engine's showcase: a first-person walk through a
small forest with a stream, meant to look like a UE or Unity scene (proposal
[future/forest-showcase.md](future/forest-showcase.md), G8d; [ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md)).
Like the [Demo](demo.md) it is a real game project built against this checkout (`MainframeEnginePath = ../..`) through
its own `Examples/Forest/Forest.slnx`, and it is **not** in `MainframeEngine.slnx`.

**Current state: the scaffold.** The project, the first-person controller and a flat test scene with a `River3D` stream
exist. The valley (terrain), trees, grass, props, audio, the pause menu, the reference shots, the benchmark and the
release job come with the later waves of the forest slice.

## Layout

```
Examples/Forest/
├── Forest.slnx, Directory.Build.props, global.json, .gitignore, .gitattributes (the Demo's + *.hdr, *.exr, *.cube),
│   NOTICE.md (no third-party assets yet), README.md
├── project.mfproj        "Mainframe Forest": main scene forest, 1600 × 900, shadows High, the input map, autoload Dev
├── Forest/               Src/Player (FirstPersonController, IFootstepSurface + SurfaceBody3D, ForestSettings),
│                         Src/World (ForestScene, SceneWriter), Src/Dev (ForestDev)
├── Forest.Desktop/       GameHost.Run(args, typeof(Forest.FirstPersonController).Assembly) + --write-scenes <dir>
├── Forest.Tests/         xUnit v3, no GPU, no LFS content
└── Content/              Scenes/forest.mscene (generated), Settings/AudioBusLayout.mres (the template's)
```

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
  wave sets it to `Terrain3D.SurfaceAt`'s layer name), else `DefaultSurface` ("default"). Sound sets come with the audio
  step.
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

## `ForestDev` and `--autowalk`

The `Dev` autoload (`ForestDev`) is idle unless started with game arguments after `++`:

- `--autowalk` presses the move actions along `ForestDev.AutoWalkPath` (walking, turning, sprinting, a jump, wading up the
  stream and out over the ramp; no crouch, which rebuilds the capsule), then measures a 600-frame window after 180
  warm-up frames with `GC.GetAllocatedBytesForCurrentThread` on the main thread (the whole frame: tree, physics,
  rendering, UI) and quits with exit code 0 when it allocated nothing, 1 otherwise (an `[ERROR]` line). With
  `--fixed-fps 60` the walk is the same on every run. Measured: 0 B, 21 footsteps, 0 gen-0 collections.
- `--no-capture` leaves the mouse free.

## Recipes

| Recipe | What |
|---|---|
| `just forest *args` | `dotnet run --project Examples/Forest/Forest.Desktop -c Release -- {{args}}` |
| `just forest-test` | `dotnet test Examples/Forest/Forest.Tests` |
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
  wadeable in the trench; `--autowalk` drives the actions.

Not in CI yet (the G8d.9 CI step and `build/forest-smoke.sh` come with the release work).

## Related

[Water](water.md) · [Demo](demo.md) · [Cameras & input](cameras-and-input.md) · [Physics](physics.md) ·
[Projects & GameHost](project-and-gamehost.md) · [future/forest-showcase.md](future/forest-showcase.md)
