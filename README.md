<p align="center"><img src="docs/images/brand/logo.svg" width="128" alt="Mainframe Engine logo"></p>
<h1 align="center">Mainframe Engine</h1>

<p align="center"><b>A C# game engine on Vulkan with a Godot-style node tree, and an editor built on its own game UI.</b></p>

<p align="center">
  <a href="https://www.nuget.org/packages/MainframeEngine"><img src="https://img.shields.io/nuget/v/MainframeEngine?logo=nuget&label=MainframeEngine" alt="MainframeEngine on NuGet"></a>
  <a href="https://www.nuget.org/packages/MainframeEngine.Templates"><img src="https://img.shields.io/nuget/v/MainframeEngine.Templates?logo=nuget&label=templates" alt="MainframeEngine.Templates on NuGet"></a>
  <a href="https://www.nuget.org/packages/MainframeEngine"><img src="https://img.shields.io/nuget/dt/MainframeEngine?logo=nuget&label=downloads" alt="NuGet downloads"></a>
  <a href="https://github.com/Mainframe-Games/mainframe-engine/releases/latest"><img src="https://img.shields.io/github/v/release/Mainframe-Games/mainframe-engine?logo=github&label=editor" alt="Latest editor release"></a>
  <a href="https://github.com/Mainframe-Games/mainframe-engine/actions/workflows/ci.yml?query=branch%3Amain"><img src="https://github.com/Mainframe-Games/mainframe-engine/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://engine.mainframe.games/docs/"><img src="https://img.shields.io/badge/docs-engine.mainframe.games-blue?logo=astro" alt="Documentation"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Vulkan-1.2-AC162C?logo=vulkan" alt="Vulkan 1.2">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Mainframe-Games/mainframe-engine" alt="MIT licence"></a>
</p>

<p align="center">
  <a href="https://engine.mainframe.games/"><b>Website</b></a> ·
  <a href="https://engine.mainframe.games/docs/"><b>Docs</b></a> ·
  <a href="docs/getting-started.md"><b>Getting started</b></a> ·
  <a href="https://github.com/Mainframe-Games/mainframe-engine/releases/latest"><b>Download the editor</b></a> ·
  <a href="docs/milestones.md"><b>Roadmap</b></a>
</p>

Mainframe Engine is a modular game engine written in C# for .NET 10. Games are trees of nodes, as in Godot: scenes are
JSON, nodes are plain C# classes whose properties a source generator registers, and servers own everything behind them
— a Vulkan renderer with HDR, PBR and a full post-processing stack, Jitter2 and Box2D physics, a SoundFlow audio mixer,
RmlUi (HTML/CSS) game UI, multiplayer replication, gettext localization, save games and Steam. The editor is built with
the engine's own game UI: scenes and inspectors, Play in its own process with C# code reload, a music mode (a DAW over
the whole window) and a sound designer, and a video mode that turns gameplay recordings into promo footage.

It runs on macOS (Apple Silicon), Windows x64 and Linux x64, ships as the
[`MainframeEngine`](https://www.nuget.org/packages/MainframeEngine) NuGet package and a self-updating editor, and is
open source under the MIT licence. It started as a way to learn how engines work and to build a framework I like
working in, and it is in active development (milestones M0–M10 are done; see the [roadmap](docs/milestones.md)). Feel
free to use it, and to send pull requests or feature requests in Issues 😁

![The editor: a 3D scene with a spot light selected, its inspector, the FileSystem and Output panels](docs/images/editor.png)

---

## Showcase

Every image is the engine rendering in real time.

### The Demo

The [Demo](Examples/Demo) has one scene per feature, switched from its nav bar; F12 opens the developer overlay in any
game.

<table>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/basic_3d.png" width="380" alt="Basic 3D"><br><b>Basic 3D</b> — primitives, glTF, sky, cascaded and spot shadows</td>
    <td align="center" valign="top"><img src="docs/images/demo/basic_2d.png" width="380" alt="Basic 2D"><br><b>Basic 2D</b> — canvas items, gradients, sprites</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/physics_3d.png" width="380" alt="Physics 3D"><br><b>Physics 3D</b> — Jitter2, save slots with thumbnails</td>
    <td align="center" valign="top"><img src="docs/images/demo/physics_2d.png" width="380" alt="Physics 2D"><br><b>Physics 2D</b> — Box2D</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/audio_3d.png" width="380" alt="Audio 3D"><br><b>Audio 3D</b> — attenuation, low-pass, doppler</td>
    <td align="center" valign="top"><img src="docs/images/demo/audio_2d.png" width="380" alt="Audio 2D"><br><b>Audio 2D</b> — panning past a 2D listener, one-shots</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/ui.png" width="380" alt="UI"><br><b>UI</b> — RmlUi widgets, data binding, hot reload</td>
    <td align="center" valign="top"><img src="docs/images/demo/spine.png" width="380" alt="Spine"><br><b>Spine</b> — under a 3D or 2D camera</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/sound_fx.png" width="380" alt="Sound FX"><br><b>Sound FX</b> — ZzFX presets, randomize/mutate, a looping song</td>
    <td align="center" valign="top"><img src="docs/images/demo/settings.png" width="380" alt="Settings"><br><b>Settings</b> — the drop-in settings menu: volumes, rebinding, display</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/particles.png" width="380" alt="Particles"><br><b>Particles</b> — a campfire's glowing flames and smoke, rain</td>
    <td align="center" valign="top"><img src="docs/images/demo/animation_3d.png" width="380" alt="Animation 3D"><br><b>Animation 3D</b> — a skinned glTF character: clips, crossfades, events, a torch on its hand</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/demo/shaders.png" width="380" alt="Shaders"><br><b>Shaders</b> — Godot spatial shaders: a procedural ground, swaying stalks, dissolving props</td>
    <td></td>
  </tr>
</table>

### The editor

A Godot-style editor whose UI is built with the engine's own RmlUi game UI. It works on game projects: a Project
Manager and New Project wizard, a scene tree, an inspector generated from the nodes' `[Export]` metadata with undo/redo,
a 3D or 2D viewport with GPU picking and gizmos, a FileSystem panel (a Simplified view of just Assets and Scripts, or the
whole project folder) that keeps references when files move, and Play — the game builds and runs in its own process,
its log streams into Output, and code changes reload into the open editor. Scenes open in tabs, each in its own world;
nothing but `[Tool]` nodes runs while editing.

<table>
  <tr>
    <td align="center" valign="top"><img src="docs/images/editor-project-manager.png" width="380" alt="The Project Manager with recent projects"><br><b>Project Manager</b> — recent projects, New Project, the Demo download</td>
    <td align="center" valign="top"><img src="docs/images/editor-add-node.png" width="380" alt="The create dialog: favourites, recent types, a searched inheritance tree and the type's description"><br><b>Create New Node</b> — favourites, recents, a searchable type tree</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/editor-game-project.png" width="380" alt="A new game project in the editor after a code reload: the Spinner's new Wobble property in the inspector"><br><b>Code reload</b> — add an <code>[Export]</code> in C#, Build &amp; Reload, edit it</td>
    <td align="center" valign="top"><img src="docs/images/editor-sound-designer.png" width="380" alt="The inspector's ZzFX sound designer on the Demo's explosion: waveform, presets, randomize/mutate and the parameters"><br><b>Sound designer</b> — ZzFX sounds: presets, randomize, mutate, waveform</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/editor-song.png" width="380" alt="Music mode on the Demo's song: the transport in the toolbar, four ZzFX tracks in the arrangement and the Lead clip in the piano roll"><br><b>Music mode</b> — a full-window DAW: arrangement, piano roll, mixer, built-in effects, VST3, MIDI</td>
    <td align="center" valign="top"><img src="docs/images/editor-video.png" width="380" alt="Video mode on a narrated recording of the Demo's Physics 2D scene: the preview, the transcript with the filler words struck through, and the timeline with silences and fillers cut"><br><b>Video mode</b> — trim recordings by transcript, cut silence and fillers</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/editor-song-effects.png" width="380" alt="The Channel EQ's plugin window on the Demo song's master: the curve with its band handles and a column of knobs per band"><br><b>Built-in effects</b> — 15 effects: EQ with a draggable curve, dynamics, tempo-synced delay and modulation</td>
    <td align="center" valign="top"><img src="docs/images/editor-song-browser.png" width="380" alt="Music mode with the Browser open on the right (songs and plugins by category) and the mixer below, its strips showing insert slots"><br><b>Browser and mixer</b> — songs and plugins (F), drag onto a track; insert slots per strip</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/editor-song-synth.png" width="380" alt="The Mainframe Wavetable window on the Demo song's Lead track playing the Evolving Pad preset: two stacked wavetables, the filter curve, sub, noise and voicing, LFO 1's editor, the modulation strip and a keyboard"><br><b>Wavetable synth</b> — two wavetable oscillators, filters, drag-to-modulate, presets</td>
    <td></td>
  </tr>
</table>

**Video mode:** Run › Play and Record (F10) records the game to MP4, and the Game|Video toggle turns the editor into a
video editor that trims it for promo footage: Detect Silence and Detect Idle Gameplay cut dead air and idle play,
Transcribe writes a Whisper transcript you edit by clicking words, Remove Fillers cuts the "um"s, and Export writes the
trimmed MP4 and the transcript as SRT or text.

### The Forest

The [Forest](Examples/Forest) is a first-person walk through a generated 256 m valley: a stream over a fall, through
three pools and under a log bridge into a pond, 1 846 Ez Tree trees and dense ground cover, all generated at load. PBR
materials lit by a physical sky and its image-based light, baked light probes, volumetric fog, histogram auto exposure,
AgX and a colour-grading LUT, at 2560 × 1440 with 3D at 75 % and TAA upscaling.

<table>
  <tr>
    <td align="center" valign="top"><img src="docs/images/forest/readme-glade.jpg" width="380" alt="Forest glade"><br><b>Glade</b> — volumetric fog shafts, back-lit translucent meadow grass</td>
    <td align="center" valign="top"><img src="docs/images/forest/readme-woodland.jpg" width="380" alt="Forest woodland"><br><b>Woodland</b> — PCSS soft shadows, contact shadows, GTAO, light probes</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/forest/readme-pond.jpg" width="380" alt="Forest misty pond"><br><b>Misty pond</b> — screen-space reflections, haze over impostor canopies</td>
    <td align="center" valign="top"><img src="docs/images/forest/readme-fall.jpg" width="380" alt="Forest fall"><br><b>The fall</b> — waterfall spray, refraction and caustics on the gravel bed</td>
  </tr>
  <tr>
    <td align="center" valign="top"><img src="docs/images/forest/readme-upstream.jpg" width="380" alt="Forest stream"><br><b>Upstream</b> — the carved stream between birches, seen through the ripples</td>
    <td align="center" valign="top"><img src="docs/images/forest/readme-ferns.jpg" width="380" alt="Forest fern floor"><br><b>Fern floor</b> — 32-tap depth of field, near and far, over moss and ferns</td>
  </tr>
</table>

---

## Features

Every subsystem has a design doc that describes it exactly as it is in the code.

### Rendering

| Feature | What you get |
|---|---|
| [Vulkan renderer](docs/design/vulkan-renderer.md) | Vulkan 1.2 on macOS (MoltenVK), Windows and Linux: HDR with linear lighting, an in-house GPU allocator and upload queue, a persisted pipeline cache, zero allocations per frame |
| [Materials & meshes](docs/design/materials-and-meshes.md) | PBR `StandardMaterial3D`, sRGB-aware textures with GPU mips, instanced and sorted batches (10 000 instances in a few draws), frustum culling, GPU picking, `SubViewport`s, billboarded `Sprite3D`s that cast shadows, outlined `Label3D` name tags |
| [Lighting & sky](docs/design/lighting.md) | Directional, omni and spot lights; procedural, physical, panoramic and cubemap [skies](docs/design/sky.md) with image-based lighting; baked light probes |
| [Shadows](docs/design/shadow-system.md) | Four sun cascades, a spot/secondary-directional atlas, point-light cubes, PCF and PCSS soft shadows, cutout casters, per-light settings |
| [Post-processing](docs/design/post-processing.md) | SSAO/GTAO, TAA and upscaling, volumetric fog, depth of field, auto exposure, glow, light shafts, FXAA, colour grading, ACES and AgX |
| [2D canvas & text](docs/design/canvas.md) | Godot-compatible canvas items, sprites, skinned polygons with bones, canvas groups that fade as one image, layers, z-order and y-sort, wrapped multi-line TrueType text from the engine's own rasterizer; Godot's `BitMap` and `Geometry2D` (alpha to polygons, polygon booleans and offsets) |
| [Spine animation](docs/design/spine.md) | Spine skeletons under a 2D or 3D camera, lit and casting alpha-tested silhouette shadows, optionally billboarded to the camera, with animation tracks and queues |
| [3D particles](docs/design/particles-3d.md) | `GpuParticles3D` on Godot's `ParticleProcessMaterial`: point, sphere, box and ring emission, spread, gravity, damping, turbulence, scale and colour curves, hue variation; camera-facing or velocity-aligned billboards, mixed or additive HDR particles that fog and glow, sorted back to front; deterministic seeds; 8 000 particles in under half a millisecond of CPU |
| [Skeletal animation](docs/design/animation.md) | Skinned glTF/FBX characters with their clips: `Skeleton3D`, GPU (compute) skinning that shadows, outlines and picking all see, an `AnimationPlayer` with crossfades, queues and events (also on a headless server), `BoneAttachment3D` |
| [Slang shaders](docs/design/shaders.md) | Every shader in Slang, compiled to SPIR-V at build time, with shared includes and light limits generated from one file; your own Godot `canvas_item` and `spatial` shaders on sprites and meshes, lit, shadowed and pickable |

### Worlds

| Feature | What you get |
|---|---|
| [Terrain](docs/design/terrain.md) | `Terrain3D`: chunked heightmap meshes with LOD, splat material layers, foliage scatter, exact collision and height queries |
| [Procedural trees](docs/design/procedural-trees.md) | A port of Ez Tree: 15 presets, realistic or low-poly styles, wind, levels of detail and impostors, from a seed |
| [Water](docs/design/water.md) | Streams carved into the terrain along a curve, ponds and falls with spray, refraction, screen-space reflections and caustics |
| [Asset import](docs/design/asset-pipeline.md) | glTF, FBX and OBJ through Assimp into scenes you can instance, with import settings, an import cache and stable UIDs |

### Gameplay

| Feature | What you get |
|---|---|
| [Scene system](docs/design/scene-graph-and-nodes.md) | A Godot-style `SceneTree`: lifecycle callbacks, signals, groups, `NodePath`, pause, `[Tool]` nodes; `.mscene`/`.mres` JSON with stable UIDs and nested scenes; `[Export]` metadata from a Roslyn source generator, no runtime reflection |
| [Physics](docs/design/physics.md) | Jitter2 in 3D and Box2D in 2D: rigid, static and character bodies, areas that detect bodies and other areas, 2D pin and spring joints, layers, queries, `MoveAndSlide`, interpolation, debug draw |
| [Input & cameras](docs/design/cameras-and-input.md) | Named input actions for keyboard, mouse and gamepad, player rebinding, captured and window-confined mouse modes, 2D, 3D and fly cameras |
| [Saves & settings](docs/design/project-and-gamehost.md#save-games) | Save slots with thumbnails, versioned JSON with migrations, crash-safe writes with recovery, a drop-in settings menu |
| [Multiplayer](docs/design/networking.md) | Generated `[Replicated]` and `[Rpc]`, delta snapshots, interpolation, authority, late join; ENet, Steam relay sockets, loopback and simulated bad networks |
| [Localization](docs/design/localization.md) | gettext catalogs with plurals and contexts, runtime locale switching, translated UI and scenes, a pseudo-locale, `mf-l10n` tooling |
| [Steamworks](docs/design/steamworks.md) | Lobbies, invites, rich presence, overlay and achievements over Facepunch.Steamworks, with Valve's natives for Windows, Linux and macOS |

### Audio

| Feature | What you get |
|---|---|
| [Audio engine](docs/design/audio.md) | A bus mixer on its own thread, 2D and 3D players, streaming WAV, OGG, MP3 and FLAC, attenuation, low-pass and doppler, a null device fallback |
| [Sound designer](docs/design/editor.md#sound-designer) | ZzFX sounds synthesised at run time: presets, randomize, mutate, a live waveform and export to WAV, in the inspector |
| [Music editor](docs/design/editor.md#song-tab-music-editor) | A DAW in the editor's music mode, over the whole window: arrangement, piano roll and mixer (resizable, P / X), a Browser of songs and plugins (F), a built-in [wavetable synth](docs/design/future/wavetable-synth.md) (unison, warps, two filters, mod matrix, 39 presets, `.wav` wavetable import), 15 [built-in effects](docs/design/music-effects.md) with plugin windows (EQ curve, gain-reduction meters, tempo-synced delays and LFOs), VST3 instruments and effects, MIDI recording, render to OGG or WAV |

### Tools

| Feature | What you get |
|---|---|
| [Editor](docs/design/editor.md) | Project Manager, scene tree, generated inspector with undo, 3D/2D gizmos, FileSystem panel, Play in its own process, C# code reload |
| [Gameplay recording](docs/design/recording.md) | Record any game to MP4 through your own ffmpeg: F10 in the editor or `--record`, live or offline, with a sidecar of input, loading and pauses |
| [Video mode](docs/design/video-editor.md) | The editor trims recordings for promo footage: detect silence and idle play, Whisper transcripts, remove filler words, export |
| [Game UI](docs/design/game-ui.md) | RmlUi (HTML and CSS) on Vulkan: data binding, gamepad navigation, hot reload and a widget library; the editor is built with it |
| [Developer overlay](docs/design/dev-overlay.md) | F12 in any game: frame, renderer, shadow maps, GPU memory, audio mixer, physics and network panels; add your own in one call |
| [Games in C#](docs/design/project-and-gamehost.md) | `dotnet new mfgame`, `project.mfproj` and `GameHost`: input actions, autoloads, structured logs, the editor link, headless dedicated servers |
| [NuGet packages](docs/design/distribution-nuget.md) | The engine and the project template on nuget.org: build a game with the .NET SDK alone, no checkout or editor needed |
| [Self-updating editor](docs/design/editor-updates.md) | The editor finds new releases on GitHub and updates itself in place on macOS, Windows and Linux |
| [Quality](docs/design/testing.md) | Zero warnings, 1 600+ tests, golden-image render tests on MoltenVK and lavapipe, a Vulkan validation gate and a 0-bytes-per-frame allocation gate |

---

## Tech stack

| Area | Technology | Notes |
|---|---|---|
| Language & runtime | C# on [.NET 10](https://dotnet.microsoft.com/) | Nullable, warnings as errors; `unsafe` for Vulkan buffers and matrices |
| Graphics | [Vulkan](https://www.vulkan.org/) 1.2 via [Silk.NET](https://github.com/dotnet/Silk.NET) 2.23 | In-house GPU allocator, upload and deletion queues, pipeline cache |
| Vulkan on macOS | [MoltenVK](https://github.com/KhronosGroup/MoltenVK) (`Silk.NET.MoltenVK.Native`) | Bundled: nothing to install to run |
| Windowing & input | [SDL2](https://www.libsdl.org/) via Silk.NET | Keyboard, mouse, gamepads; HiDPI |
| Shaders | [Slang](https://shader-slang.org/) → SPIR-V | Compiled by `dotnet build` (`slangc`); committed SPIR-V as the fallback |
| Game & editor UI | [RmlUi](https://github.com/mikke89/RmlUi) 6.3 + [FreeType](https://freetype.org/) | Through `mfrmlui`, an in-house C ABI shim, and a Vulkan renderer |
| Physics | [Jitter2](https://github.com/notgiven688/jitterphysics2) 2.9 (3D) · [Box2D.NET](https://github.com/ikpil/Box2D.NET) 3.1 (2D) | Behind `PhysicsServer3D` / `PhysicsServer2D` |
| Audio | [SoundFlow](https://github.com/LSXPrime/SoundFlow) 1.4 (miniaudio) · [NVorbis](https://github.com/NVorbis/NVorbis) 0.10 | Mixer on its own thread; OGG decoding; [ZzFX](https://github.com/KilledByAPixel/ZzFX) synthesis |
| Networking | [ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp) 2.4 | ENet natives built in-house |
| Code generation | Roslyn source generator | Node/resource registration, replication and RPCs: no reflection |
| Localization | [GetText.NET](https://github.com/perpetualKid/GetText.NET) 10.0 + `mf-l10n` | `.po` → `.mo` compiled at build time |
| Models & images | [Assimp](https://github.com/assimp/assimp) via Silk.NET · [StbImageSharp](https://github.com/StbSharp/StbImageSharp) 2.30 | glTF, FBX and OBJ models; PNG and JPEG textures |
| Skeletal animation | [Spine](https://esotericsoftware.com/) C# runtime | Vendored submodule ([licence](#license)) |
| Steam | [Facepunch.Steamworks](https://github.com/Facepunch/Facepunch.Steamworks) 2.4.1 + Valve's `steam_api` | Optional; only active when Steam runs |
| Recording | [ffmpeg](https://ffmpeg.org/) as a child process | Your own ffmpeg; the engine never links an encoder |
| Video mode | [Whisper.net](https://github.com/sandrohanea/whisper.net) 1.9 | Local speech recognition, in the editor only |
| Tests | [xUnit](https://xunit.net/) v3 · [BenchmarkDotNet](https://benchmarkdotnet.org/) | Golden images on MoltenVK and Mesa's lavapipe |
| Build & CI | [`just`](https://github.com/casey/just) · GitHub Actions | Linux, Windows and macOS; Docker for lavapipe |
| Docs & website | [Astro](https://astro.build/) + [Starlight](https://starlight.astro.build/) | Generated from the Markdown in [`docs/`](docs/README.md) |

Versions are central in [`Directory.Packages.props`](Directory.Packages.props).

---

## Get started

Download the editor from [Releases](https://github.com/Mainframe-Games/mainframe-engine/releases/latest), or make a
game with the .NET 10 SDK alone:

```bash
dotnet new install MainframeEngine.Templates
dotnet new mfgame -n MyGame
```

**[Getting started](docs/getting-started.md)** covers installing the editor, building from source, making a game in
the editor, the Demo, a tour of the code, tests and releases.

## Documentation & roadmap

- **[Docs site](https://engine.mainframe.games/docs/)** — the documentation, searchable, with the
  landing page and editor downloads at [engine.mainframe.games](https://engine.mainframe.games/).
- **[Design docs](docs/README.md)** — how each subsystem works today, one topic per file, with known issues.
- **[Milestones](docs/milestones.md)** — the roadmap. M0–M10 are done; next are M11 (backend-neutral GPU API, WebGPU),
  M12 (mobile core: Android + iOS) and M13 (mobile platform services), each with a design proposal.

---

## License

- **Engine code** (everything in this repository unless noted otherwise) is MIT licensed — see [LICENSE](LICENSE).
- **Third-party components and assets** keep their own licences; they are listed, with their notices, in
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Keep those notices with any distribution of the engine, the editor
  or a game built with it.
- **Spine:** `Plugins/Spine` (spine-csharp) is under the
  [Spine Runtimes License](https://esotericsoftware.com/spine-runtimes-license), **not MIT**. A game that uses
  `SpineNode` (or anything else from the Spine Runtimes) needs its own
  [Spine Editor licence](http://esotericsoftware.com/spine-editor-license). The bundled Spine example assets
  (SpineBoy) are © Esoteric Software and are not covered by the MIT licence.
