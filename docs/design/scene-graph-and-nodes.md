# Scene Graph & Nodes

## Purpose

Everything in a game is a **node in a tree**, as in Godot. Behaviour comes from node *types* (C#
subclasses); scenes are reusable subtrees saved as data ([Scene serialization](scene-serialization.md)).
The engine owns a `SceneTree` and runs the frame for every node inside it: lifecycle callbacks,
fixed-step physics process, process, deferred calls and frees, transform sync, input routing, groups and
signals. Nodes are thin front-ends to engine **servers** (rendering and audio now; physics and UI later):
the node holds editable state, the server holds GPU/physics/audio objects.

Names follow Godot (`Node3D`, `Camera3D`, `OmniLight3D`, `WorldEnvironment`, `QueueFree`, `NodePath`,
…) with two exceptions recorded in the [Godot-names ADR](../../memory/decisions/0010-godot-node-names.md):
the root is `SceneViewport` (not `Viewport`, which the renderer uses unqualified for
`Silk.NET.Vulkan.Viewport`) and lifecycle methods use the engine's `On*` prefix.

## Class hierarchy

```mermaid
classDiagram
    class Node {
        +string Name
        +NodeId Id
        +Node? Parent
        +IReadOnlyList~Node~ Children
        +Node? Owner
        +SceneTree? Tree
        +ProcessMode ProcessMode
        +int ProcessPriority
        +AddChild(Node) / RemoveChild / MoveChild / Reparent
        +GetNode~T~(NodePath) / FindChild / GetPath / GetPathTo
        +AddToGroup(string, bool persistent)
        +Connect(signal, target, method, flags)
        +QueueFree() / Free() / CallDeferred(Action)
        #OnEnterTree() / OnReady() / OnExitTree()
        #OnProcess(in GameTime) / OnPhysicsProcess(float)
        #OnInput(InputEvent) / OnUnhandledInput(InputEvent)
    }
    class Node3D {
        +Vector3 Position
        +Quaternion Rotation
        +Vector3 RotationDegrees
        +Vector3 Scale
        +bool Visible
        +Transform3D Transform / GlobalTransform
        +Matrix4x4 ModelMatrix
        +LookAt(Vector3 target, Vector3? up)
    }
    class Node2D {
        +Vector2 Position
        +float Rotation
        +Vector2 Scale
        +int ZIndex
        +Transform2D GlobalTransform
    }
    class SceneViewport { +World3D World3D +World2D World2D +Camera3D? ActiveCamera3D }
    class VisualInstance3D { +bool CastShadows +int RenderPriority +Draw() +DrawShadow2D() +DrawShadowPoint() }
    Node <|-- Node3D
    Node <|-- Node2D
    Node <|-- SceneViewport
    Node <|-- WorldEnvironment
    Node <|-- Timer
    Node <|-- NetworkNode
    Node <|-- MissingNode
    Node3D <|-- VisualInstance3D
    Node3D <|-- Camera3D
    Node3D <|-- Light3D
    Light3D <|-- DirectionalLight3D
    Light3D <|-- OmniLight3D
    Light3D <|-- SpotLight3D
    VisualInstance3D <|-- GeometryInstance3D
    GeometryInstance3D <|-- MeshInstance3D
    GeometryInstance3D <|-- Sprite3D
    SceneViewport <|-- SubViewport
    VisualInstance3D <|-- SpineNode
    VisualInstance3D <|-- Grid3D
    Node2D <|-- Camera2D
```

## Key types

| Type | File | Notes |
|---|---|---|
| `Node` | [Scene/Node.cs](../../MainframeEngine/Src/Scene/Node.cs) (+ `.Processing`, `.Groups`, `.Signals`) | tree, names, paths, lifecycle, groups, signals, freeing |
| `SceneTree`, `SceneTreeTimer` | [Scene/SceneTree.cs](../../MainframeEngine/Src/Scene/SceneTree.cs) | frame loop, process lists, groups, deferred queue, `NodeId` registry |
| `SceneViewport`, `World3D`, `World2D` | [Scene/](../../MainframeEngine/Src/Scene/) | root viewport, render scenario |
| `Node3D`, `Node2D`, `Transform3D`, `Basis`, `Transform2D`, `EulerAngles` | [Scene/](../../MainframeEngine/Src/Scene/) | cached, dirty-flagged transforms |
| `NodePath`, `NodeId`, `ProcessMode`, `Timer`, `MissingNode` | [Scene/](../../MainframeEngine/Src/Scene/) | |
| `InputEvent*`, `InputRouter` | [Scene/Input/](../../MainframeEngine/Src/Scene/Input/) | SDL/Silk input → tree |
| `VisualInstance3D`, `Camera3D`, `Light3D` (+3), `WorldEnvironment`, `Sky`, `Grid3D` | [Scene/Nodes3D/](../../MainframeEngine/Src/Scene/Nodes3D/) | render front-ends |
| `Camera2D` | [Scene/Nodes2D/](../../MainframeEngine/Src/Scene/Nodes2D/) | |
| `IServer`, `ServerRegistry`, `RenderServer` | [Servers/](../../MainframeEngine/Src/Servers/) | |
| `GeometryInstance3D`, `MeshInstance3D`, `Sprite3D` | [Scene/Nodes3D/GeometryInstance3D.cs](../../MainframeEngine/Src/Scene/Nodes3D/GeometryInstance3D.cs) | batched mesh drawables — see [Materials & meshes](materials-and-meshes.md) |
| `SubViewport` | [Scene/SubViewport.cs](../../MainframeEngine/Src/Scene/SubViewport.cs) | offscreen view with its own world |
| `SpineNode`, `NetworkNode` | [Nodes/](../../MainframeEngine/Src/Nodes/) | existing drawables, retargeted onto `VisualInstance3D` |

## The tree

- **Children** are ordered (`Children`, `GetChild`, `GetIndex`, `MoveChild`, `AddSibling`).
  `AddChild` rejects a node that already has a parent and cycles; `Reparent(newParent,
  keepGlobalTransform: true)` moves a subtree keeping its world transform.
- **Names** are unique among siblings. An empty name becomes the type name; a collision appends a number
  (`Box` → `Box2`, `Box2` → `Box3`). `/ : @ % "` are replaced with `_`. Up to 8 children are searched
  linearly; above that a name index is kept (span-keyed, so lookups do not allocate).
- **Owner** is the scene root a node was saved with; it must be an ancestor and is cleared when a removal
  breaks that. The scene saver writes only nodes owned by the root, so runtime-spawned nodes are never
  saved. `UniqueNameInOwner` makes a node reachable as `%Name` from nodes sharing its owner.
- **`NodePath`**: relative (`"Camera"`, `"../Player/Camera"`, `"."`) or absolute (`"/root/Main"`), with
  `%Name` segments. `GetNode<T>` throws on a missing node or wrong type, `GetNodeOrNull<T>` does not.
  Resolution walks spans and does not allocate. `FindChild(pattern)` / `FindChildren<T>` match `*`/`?`
  wildcards (owned nodes only by default, as in Godot). `GetPath()` / `GetPathTo(node)` build paths.
- **Freeing**: `Free()` removes the node (exiting the tree), frees its children, disconnects its named
  signal connections and calls `Dispose(bool)` for resource release; `Dispose()` is `Free()` so nodes work
  with `using`. `QueueFree()` defers the free to the end of the frame (immediate outside a tree).
  `Node.IsInstanceValid(node)` is Godot's `is_instance_valid`.
- **Identity**: every node gets a runtime-unique `NodeId` (never reused). `SceneTree.Find(NodeId)` looks
  it up through a dictionary maintained on enter/exit. `NodeId`s are per process, so replication uses its own
  network ids (`Node.NetworkId`, assigned by the server, see [Networking](networking.md#replication)). Scene files
  reference nodes by path, never by id.

## Lifecycle

```mermaid
sequenceDiagram
    autonumber
    participant P as parent.AddChild(child)
    participant C as child subtree
    participant S as Servers
    P->>C: Parent, Tree, viewport, resolved ProcessMode set
    C->>C: OnEnterTree() — top-down; TreeEntered; parent's ChildEnteredTree
    C->>S: visuals/lights/cameras register with their World3D / RenderServer
    C->>C: OnReady() — bottom-up, once per node; Ready
    loop every frame while inside the tree
        C->>C: OnPhysicsProcess(fixedDt) × N fixed steps
        C->>C: OnProcess(gameTime)
    end
    P->>C: RemoveChild / QueueFree
    C->>C: OnExitTree() — bottom-up; TreeExiting; parent's ChildExitingTree
    C->>S: unregister
    C->>C: TreeExited (after removal)
```

- `OnReady` runs once per node (`RequestReady()` re-arms it) after all its children are ready — the
  place for `GetNode<T>()`. Children added inside `OnEnterTree`/`OnReady` enter immediately.
- Callbacks are `protected virtual`. Process callbacks are enabled exactly when a type overrides them
  (found once per type, cached); `SetProcess`, `SetPhysicsProcess`, `SetProcessInput` and
  `SetProcessUnhandledInput` toggle them.
- **`ProcessMode`**: `Inherit` (the root is `Pausable`), `Pausable`, `WhenPaused`, `Always`, `Disabled`.
  `SceneTree.Paused` stops pausable nodes (process, physics, input) and the fixed-step servers.
- **`ProcessPriority`**: lower runs first; equal priorities run in tree order. Physics process uses the
  same order.
- **Built-in signals** (`[Signal]` events): `TreeEntered`, `Ready`, `TreeExiting`, `TreeExited`,
  `Renamed`, `ChildEnteredTree`, `ChildExitingTree`; plus `Timer.Timeout`.
- **Locale changes** (M9): `Tr.SetLocale` runs `OnLocaleChanged()` on every node in every live tree, parents first,
  then `SceneTree.LocaleChanged` — at the end of the frame when it happened during `Tick`, at the next `Tick` when it
  came from another thread. `AutoTranslateMode` (exported, inherited) decides whether `Atr`/`AtrN` translate. See
  [Localization](localization.md#scene-strings).

## The frame (`SceneTree.Tick`)

| Step | What runs |
|---|---|
| 1. Physics | `accumulator += min(dt, MaxFrameDelta = 0.25 s)`; up to `MaxPhysicsStepsPerFrame = 5` steps of `1 / PhysicsTicksPerSecond` (60 Hz): `PhysicsFrame` event, `OnPhysicsProcess`, physics-callback timers, `IFixedStepServer.FixedStep` (not while paused). A backlog beyond the cap is dropped. Fixed-step servers get `BeforeFixedSteps()` before the first step of a frame and `AfterFixedSteps(PhysicsInterpolationFraction)` after the loop (both skipped while paused): the physics servers restore physics poses and write interpolated render poses there ([Physics](physics.md#fixed-timestep-and-interpolation)). |
| 2. Process | `ProcessFrame` event, `OnProcess(in GameTime)`, idle timers (`SceneTreeTimer`). |
| 3. Deferred | `CallDeferred` callbacks in order (including ones queued while flushing), then `QueueFree` frees; repeated until both queues are empty. |
| 4. Transform sync | `OnTransformChanged` for 2D/3D nodes that asked (`SetNotifyTransform`) and whose global transform changed — lights push their direction/position here. |
| 5. Frame servers | `IFrameServer.Process` (Steam callbacks, the `AudioServer`; UI later). |

Iteration is over sorted, reused snapshots, so callbacks may add, remove, free or reorder nodes: removed
or queued nodes are skipped, added ones start next frame. The lists re-sort lazily (only after the tree,
membership or a priority changed). **A steady-state tick allocates nothing** (unit-tested over 10 000
nodes; see [Testing](testing.md)).

`Engine` owns one tree (`Engine.Tree`, `Engine.Root`) and ticks it after the legacy `OnUpdate` hook; see
[Engine lifecycle](engine-lifecycle.md). Tests and tools create their own `SceneTree`.

## Transforms

`Node3D` stores local position, rotation (a normalized quaternion) and scale. `RotationDegrees` is Euler
degrees applied about X, then Y, then Z (`EulerAngles`; the order the engine always used, so old scenes
keep their look); the value set is returned unchanged until `Rotation` is set directly, so incrementing it
is stable. Decomposed angles snap within 0.0005° of a whole degree so scene files stay readable.

- `Transform` (local) and `GlobalTransform` are `Transform3D` (a `Basis` of column vectors + origin).
  `ModelMatrix` is the cached row-vector world matrix (`Scale × Rotation × Translation`) the drawables use.
- Setting any transform property marks the node's local transform dirty and its `Node3D` subtree's global
  transforms dirty, stopping at subtrees that are already dirty (a node is only cleaned after its
  ancestors, so a dirty node's subtree is dirty). Globals are recomputed lazily on read.
- Transforms chain only through `Node3D` parents: a `Node3D` under a plain `Node` is a new transform root
  (Godot semantics). `Visible` / `IsVisibleInTree()` follow the same chain.
- `LookAt(target, up)` points `-Z` at a world position, keeping position and scale; `GlobalPosition`,
  `GlobalTransform` setters compute the local transform; `TranslateObjectLocal`, `RotateObjectLocal`.
- `SetNotifyTransform(true)` queues `OnTransformChanged()` for the end of the frame whenever the global
  transform changed (once per frame, also on entering the tree).
- Engine-internal hooks (`private protected`): `TrackGlobalTransformChanges` → `OnGlobalTransformInvalidated()` runs
  synchronously when the global transform becomes dirty (own or ancestor change), and `TrackLocalTransformChanges` →
  `OnLocalTransformChanged()` on every local set. Physics bodies and collision shapes use them to push moves to their
  server without per-step scans.

`Node2D` mirrors this in 2D (`Transform2D`, rotation in radians, `RotationDegrees` serialized, `ZIndex`), plus Godot's
`Skew` (`SkewDegrees` serialized); a transform assigned to `Transform` is kept exactly and only decomposed for the getters.
`SceneTree.Tick` gives process callbacks the frame's delta minus the physics time it dropped (the `MaxFrameDelta` clamp
and the backlog beyond `MaxPhysicsStepsPerFrame`), as Godot does, so a stall reads as the steps physics ran (ADR 0114).

## Groups, deferred calls, timers

- `AddToGroup(name, persistent)` / `RemoveFromGroup` / `IsInGroup`; persistent groups are saved with
  the scene. The tree tracks members while they are inside it: `GetNodesInGroup` (tree order, live list),
  `GetFirstNodeInGroup`, `CallGroup(name, action)` and the allocation-free
  `CallGroup<TState>(name, state, static action)`, which iterate a snapshot.
- `CallDeferred(Action)` and the allocation-free `CallDeferred(Action<object?>, state)` (on nodes inside a
  tree, or on `SceneTree`) run at step 3.
- `SceneTree.CreateTimer(seconds)` returns a one-shot `SceneTreeTimer` (`Timeout` event);
  the `Timer` node (`WaitTime`, `OneShot`, `Autostart`, `ProcessCallback`) is the saveable kind.
- `SceneTree.ChangeScene(Node | PackedScene)` / `ChangeSceneToFile(pathOrUid)` replace `CurrentScene`. Outside a tick
  and outside input dispatch (start-up, tools) the swap is immediate. During a tick or a `PushInput` (UI clicks run there,
  outside the tick) it waits for Godot's scene change flush: after the frame's process callbacks and deferred calls,
  before timers ([0137](../../memory/decisions/0137-scene-change-flush.md)). So a click handler never frees or builds a
  scene inside RmlUi's dispatch, and the new scene first processes a frame later. The last change of a frame wins; an
  earlier pending scene is freed without entering the tree. `Shutdown()` frees everything.

### Tweens

`Node.CreateTween()` (bound: pauses with the node, dies when it is freed) or `SceneTree.CreateTween()` returns a
`Tween`, a port of Godot 4.7's: `TweenProperty(target, "modulate:a", value, seconds)` (Godot property paths;
snake_case → the C# property, `:x/:y/:z/:w`, `:r/:g/:b/:a` components), `TweenInterval`, `TweenCallback`, `TweenMethod`;
`SetParallel`/`Parallel`/`Chain`, `SetLoops`, `SetSpeedScale`, `SetTrans`/`SetEase` (Godot's easing equations, default
Linear / InOut), `From`, `AsRelative`, `SetDelay`, `Finished`. The tree steps tweens after timers (process or physics,
`SetProcessMode`), leftover time carried into the next step as Godot does.

## Signals

Signals are plain C# events marked `[Signal]`, so code connects with `+=` as usual. The source generator
records each one (name, delegate type, parameter types, add/remove accessors) so the editor's Signals
panel and scene files can connect **by name**:

```csharp
area.Connect("BodyEntered", player, nameof(Player.OnHit), ConnectFlags.Persist);
```

- The target method must be public or marked `[SignalHandler]`, and its parameters must accept the
  signal's. Binding uses reflection once, at connect time; emitting is a normal delegate call.
- `ConnectFlags.Persist` connections are saved with the scene and re-bound on instantiation (their
  `OriginScene` is the scene root that created them, so a sub-scene's connections are not re-saved by the
  scenes that instance it). `Deferred` runs the handler at the end of the frame, `OneShot` disconnects
  after the first emission (both through a generated forwarder; void signals up to 8 parameters).
- Named connections are removed automatically when either node is freed. `Disconnect`, `IsConnected`,
  `GetSignalConnections`, `GetIncomingConnections`.

## Edit mode (the editor)

`SceneTree.EditMode` (set by the editor, M10) runs `OnProcess`, `OnPhysicsProcess`, `OnInput` and `OnUnhandledInput`
only for nodes whose type is marked `[Tool]` (`Node.IsTool`, not inherited by subclasses), and does not step the
fixed-step servers (physics). Lifecycle callbacks, deferred calls, frees, transform sync and frame servers run as
usual, so edited scenes render, register with their worlds and can be picked, but game code does not run. Tools render a
viewport with their own camera through `SceneViewport.CameraOverride` (any `ICamera`; it never touches the scene's
cameras) and draw always-visible handles into `SceneViewport.OverlayLines` (debug lines without depth test). See
[Editor](editor.md#edit-mode-and-edited-worlds).

## Input

`InputRouter` (owned by `Engine`) turns Silk.NET/SDL keyboard, mouse and gamepad callbacks into
`InputEventKey`, `InputEventText`, `InputEventMouseButton`, `InputEventMouseMotion`,
`InputEventMouseWheel`, `InputEventGamepadButton` and `InputEventGamepadAxis` and calls
`SceneTree.PushInput`: `OnInput` in reverse tree order (children before parents), then `OnUnhandledInput`,
stopping once a node calls `GetViewport().SetInputAsHandled()`. Nodes that cannot process (pause) are
skipped. One event instance per type is reused (allocation-free): `Clone()` an event to keep it.
Before any node, `PushInput` offers the event to every `IInputServer` (M8: the `UiServer`); an event the
game UI consumed stops there and is reported handled — see [Game UI → input routing](game-ui.md#input-routing).
Nodes with `InputBeforeUi = true` get `OnInput` first, before the UI (Godot's `_input` placement, for toggles such as a
menu on Tab that a focused button would otherwise take; [ADR 0138](../../memory/decisions/0138-input-before-ui.md)).
The dev overlay is an RmlUi layer, so it is covered by the same server: a click on its panel never reaches nodes.

## Servers and render nodes

Servers replace the old static `Node.Initialize`: `Engine` registers them in `OnLoad` (after the
renderer) into `SceneTree.Servers` (`ServerRegistry`), and nodes reach them in `OnEnterTree`:

| Server | Interface | Status |
|---|---|---|
| `RenderServer` | `IServer` | M2: facade over the renderer and one shared `ShadowSystem` |
| `SteamServer` | `IFrameServer` | M2: registered when `EngineOptions.SteamAppId` ≠ 0; pumps `Steam.RunCallbacks` |
| `MultiplayerApi` | `IFrameServer` | M5: always registered (`Engine.Multiplayer`), idle until started; receives at `SceneTree.ProcessFrame`, sends in `Process` ([Networking](networking.md#frame-and-tick)) |
| `AudioServer` | `IFrameServer` | M7: registered when `EngineOptions.Audio.Enabled` (default); voices, buses, listener; see [Audio](audio.md) |
| `PhysicsServer3D`, `PhysicsServer2D` | `IFixedStepServer`, `IFrameServer` | M6: one Jitter2 / Box2D space per world; stepped after `OnPhysicsProcess`, frozen while paused; debug draw per frame ([Physics](physics.md)) |
| UI (M8) | `IFrameServer` | ticked after transform sync |

`ServerRegistry` holds one server per type (`Register`, `Get<T>`, `GetRequired<T>`, `Render`) and disposes
them in reverse order after the tree is freed.

**`RenderServer`** draws a viewport's `World3D` with its active camera
(`SceneViewport.ActiveCamera3D`, else `ActiveCamera2D`; aspect from the swapchain extent):

- `PrepareFrame(root)` (M3, before `BeginFrame`) — culls and sorts the world's `GeometryInstance3D`s (and every
  `SubViewport`'s), creates/updates their GPU meshes, materials and textures, completes finished picks.
- `RenderShadows(viewport)` — every shadow-casting, visible visual into every light's map
  (`ShadowSystem.RenderShadows<TState>` with static lambdas): batched mesh casters as instanced draws, other
  visuals through `DrawShadow2D`/`DrawShadowPoint`.
- `RenderOffscreen(root)` (M3) — `SubViewport`s and object-ID picking passes, before the main pass.
- `RenderMain(viewport)` — writes the frame's shared set 0 (`IVulkanContext.Frame.Begin(camera, World3D.Lights)`:
  camera + lights, once per frame), then the `WorldEnvironment`'s sky, visuals with a negative `RenderPriority`,
  the opaque mesh batches, the other visuals by `RenderPriority` then tree-entry order, and the transparent mesh
  batches back to front, all into the HDR scene target ([Color pipeline](color-pipeline.md)).
- `PickAsync(x, y)` / `RequestPick` + `TryGetPickResult` — GPU picking ([Materials & meshes](materials-and-meshes.md#picking-object-ids));
  `MeshStats`, `PipelineStates`, `ResidentResources` — diagnostics.
- `ShadowsEnabled = false` runs without a `ShadowSystem` (lit pipelines bind the fallback set).
- It tracks every GPU resource owner it created, so nodes removed but never freed are still released at
  shutdown.

**Render node front-ends**:

| Node | Wraps | Notes |
|---|---|---|
| `VisualInstance3D` | — | registers with `World3D` while in a tree; creates GPU objects through the server on first enter (or lazily at the first draw); releases them on `Free` or server shutdown; `CastShadows`, `RenderPriority` |
| `MeshInstance3D`, `Sprite3D` (`GeometryInstance3D`) | `MeshRenderer` | `Mesh`, `MaterialOverride`; never drawn one by one: culled, sorted and batched into instanced draws (M3, [Materials & meshes](materials-and-meshes.md)) |
| `SubViewport` | its own `World3D`, offscreen targets | rendered before the main pass; picking; shown in the UI as an `engine://` image |
| `SpineNode` | `SpineRenderer` | `Folder`, `Animation` (played on load), `SpineScale`; skeleton data loads on the CPU on first use |
| `Grid3D` | `SceneGrid3d` | debug/editor grid: no shadows, `RenderPriority` -100 |
| `Camera3D` | `PerspectiveCamera` | `Current`, `Fov`, `Near`, `Far`; looks along `-Z`; `RenderCamera` is the synced math camera |
| `Camera2D` | `OrthographicCamera` | `Current`, `Zoom`, `Distance` |
| `DirectionalLight3D`, `OmniLight3D`, `SpotLight3D` | `DirectionalLight`, `PointLight`, `SpotLight` | `Color`, `Energy`, `Range`, cone angles; direction = global `-Z`; registered in `World3D.Lights` |
| `WorldEnvironment` | `SkyEnvironment` | `Sky` resource (procedural / panoramic / cubemap + colors), `AmbientColor` |

The pre-M2 math cameras are now `PerspectiveCamera` / `OrthographicCamera` (`ICamera`), still usable
without a tree.

## Invariants

- Node constructors must be cheap and side-effect free (the type registry creates a pristine instance of
  every serialized type; scenes are instantiated outside the tree). Acquire GPU/native objects in
  `OnEnterTree` or through a server.
- Nodes are main-thread only while inside a tree.
- Draw methods run inside the server's passes (main render pass / shadow callbacks); tree-less code may
  call them by hand inside the matching pass.

## Known issues

- `SubViewport`s render 3D only, without shadows unless `Shadows` is set and the main world draws nothing (the editor).
  2D canvas items draw through the canvas renderer ([2D canvas](canvas.md)); `Camera2D` still renders 3D visuals
  orthographically and does not yet move the canvas (E9).
- Euler angles use X→Y→Z order (engine legacy), not Godot's Y→X→Z.
- Re-sorting a process list after a structural change is O(n log n); spawning every frame in a very large
  tree pays it each frame.

## Related docs

[Scene serialization](scene-serialization.md) · [Engine lifecycle](engine-lifecycle.md) ·
[Spine](spine.md) · [Lighting](lighting.md) · [Cameras & input](cameras-and-input.md) ·
[Shadow system](shadow-system.md) · [Materials & meshes](materials-and-meshes.md) ·
[Editor](editor.md)
