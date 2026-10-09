# Forest

## Purpose

[`Examples/Forest`](../../Examples/Forest/) ("Mainframe Forest") is the engine's showcase: a first-person walk through a
small forest with a stream, meant to look like a UE or Unity scene (proposal
[future/forest-showcase.md](future/forest-showcase.md), G8d; [ADR 0155](../../memory/decisions/0155-curve3d-river3d-forest-project.md)).
Like the [Demo](demo.md) it is a real game project built against this checkout (`MainframeEnginePath = ../..`) through
its own `Examples/Forest/Forest.slnx`, and it is **not** in `MainframeEngine.slnx`.

**Current state: the playable valley.** The project, the first-person controller, the CC0 art ([Assets](#assets)), the
procedural [audio](#audio) and [the valley](#the-valley): a generated 256 m terrain with a stream (a small fall, three
pools, a log bridge) running into a pond, about 1 500 trees, 180 bushes, grass, reeds, ferns, stones and props, under a
physical morning sky with fog, light shafts, eye adaptation, SSAO (GTAO, [ADR 0165](../../memory/decisions/0165-ssao-gtao.md)) and TAA. Also the [reference shots](#reference-shots), the
[benchmark](#forestdev---autowalk-and-the-benchmark) and a path-following `--autowalk`. Not yet: the pause menu and
settings page, impostors, LUT grading, dust motes and butterflies, and the release job.

![R1: the glade, looking into the morning sun](../images/forest/r1-glade.png)

## Layout

```
Examples/Forest/
├── Forest.slnx, Directory.Build.props, global.json, .gitignore, .gitattributes (the Demo's + *.hdr, *.exr, *.cube),
│   NOTICE.md (every third-party asset), README.md
├── project.mfproj        "Mainframe Forest": main scene forest, 1920 × 1080 px (contentScale 1), shadows High, TAA,
│                         the input map, autoload Dev
├── benchmark-baseline.json  this Mac's forest-bench result (1920 × 1080)
├── Forest/               Src/Player (FirstPersonController, IFootstepSurface + SurfaceBody3D, ForestSettings),
│                         Src/World (ForestScene, ForestValley, ValleyLayout, ValleyGenerator, ValleyNoise,
│                         PolylineField, ForestVegetation, SceneWriter, ForestAssets, ForestAssetGallery),
│                         Src/Dev (ForestDev, PathWalker, ForestBenchmark), Src/Audio (ForestAudio, …)
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

## The valley

`ForestScene.Build()` writes `Content/Scenes/forest.mscene` (`dotnet run --project Examples/Forest/Forest.Desktop --
--write-scenes Examples/Forest/Content/Scenes`; `ProjectTests.CommittedSceneMatchesTheBuilder` checks it byte for byte).
The scene holds only what an editor would tune: the `Sun`, the `Environment` (the look), the `Valley` node with its
knobs, the `Player` and the `Audio`. **The valley is generated when the `ForestValley` node is ready**, deterministically
from `ValleyLayout` and a seed, in about 1.2 s (Release, M5: heights 0.4 s, carve, pond and paint 0.4 s, trees 0.3 s,
props 0.1 s), plus Jitter2's ≈ 2 s first step over the terrain's 524k collision triangles. Generating beats committing
the layers as PNGs: nothing to re-commit to LFS on every tweak, nothing the tests need from LFS. Everything it builds is
an unowned child, never saved (not even its terrain's layers: `SceneSaver`'s save hooks skip unowned nodes). The node is
a `[Tool]` (ADR 0169): the editor generates the same visuals; the player's ground snap, the audio wiring and the map's
walls run in the game only.

```
                 N (z = 0)
      outcrop ▲ source ─ fall (3.5 m)
              pool 1 · falls viewpoint (R2)
   glade (R1)    pool 2            dense pine slope
   oak, ash        pool 3 ═ log bridge (R3)    lookout (R4)
                       │ aspen banks (R5)
   trailhead ●        pond (terrain water layer)
                 S (z = 256)
```

| Part | How |
|---|---|
| Layout (`ValleyLayout`) | world = terrain-local metres, north −Z. The stream's 15 points (surface, width, depth: a source in the outcrop, a 3.5 m fall, three wide deep pools between riffles, the bridge reach, the mouth), the pond (centre, radii 27 × 18 m, level 0.6 m, 1.7 m deep), outcrop, glade, the 21-point walking loop (≈ 480 m, ≈ 3 min at walk speed), spawn, sun (azimuth 105°, elevation 21°), tree clearings and view corridors, the reference shots |
| Heights (`ValleyGenerator`) | the floor follows the stream's downhill-clamped surface near it and a ±24 m smoothed profile away from it (the fall stays a local step), +0.4 m and 4 % per metre across; the west ridge rises to ≈ +30 m (gentler in the glade), the east to ≈ +46 m; the valley closes in the north and rises past the pond; fBm rolling ground, quieter near water and in the glade; the outcrop is a ridged plateau (+9 m); the pond basin is an ellipse with a noise-wobbled shore; the **path bed** is level across, follows the ground's smoothed profile along it and blends into the slopes over 5 m (cut and fill), sunk 6 cm; the banks rise 1.25 m above the water at the bridge, a small gorge the log spans |
| Stream | `River3D` from the layout's points plus two jittered points per reach (wandering banks), `SectionLength` 0.75 m, 6 cross segments, carved (`Carve()`, `CarveId` "stream", `BankWidth` 2.2, `ShoreLift` 0.06); heights come from the layout, so no `FitToTerrain` |
| Pond | after the carve, `ApplyPond` floods every basin vertex below the level (`TerrainData` water layer, `MaxWaterDepth` 2 m) |
| Splat (8 layers, `ForestAssets` order) | per cell from the final ground: needles under the pines, leaf litter on the aspen bank and forest edges, grass in the glade, patchy moss; then overrides: moss at rock bases, rock above ≈ 37° and on the outcrop's steep, convex parts, moss 2–5 m from water, mud within ≈ 1.5 m of the stream and the pond, gravel in the stream bed and the pond's deep bed, gravel path edges, dirt on the path |
| Terrain material | `ForestAssets.CreateTerrainMaterial()` with every ORM's roughness lifted to 0.6 + 0.4 r (`RoughenOrm`: the scans' 0.45 made dry ground glare white against a low sun), grass and leaves tinted, `AntiTiling` off (3.5 ms at 1080p), detail to 45 m, far from 110 m |
| Ground cover (`TerrainData.FoliageTypes`) | meadow grass (8/m² on grass, to 32 m), tall grass, woodland grass on leaves and moss, reeds on the pond's mud, the Poly Haven fern (merged, re-centred, with a wind stream: `FoliageMaterial3D`, cut-out) on needles, leaves and moss, `rock_07` stones on gravel and moss; densities × `GroundCoverDensity` (0.8) |
| Trees (`ForestVegetation`) | one jittered candidate per 4 m cell, accepted by zone: pines (large, medium, small; 2 + 2 + 1 seeds) on the east slope, outcrop, north and the high west ridge; aspens (5 variants) along the stream and round the pond; ash at the glade's edge and in mixed woods; a few oaks in the glade. Clear of the path (3.2 m), water (1.6 m), the bridge (7 m), steep rock, the clearings and the R2/R4 view corridors. ≈ 1 500 trees in a `TreeScatter` (levels at 22 m and 55 m, out to 400 m; level 0 casts into the near cascades, level 1 into every cascade and the far shadow from any distance: `ShadowCoarseLod` 1, ADR 0167); ≈ 180 bushes in a second one (to 75 m, no collision, only level 0 casts) |
| Props | `ForestAssets.InstantiateProp` with shared PBR materials: the bridge (`dead_tree_trunk_02` × 1.6 along the path's crossing, its top 14 cm above the banks, a 0.9 m walkway box tagged `wood`), mossy rock sets at the source and the fall, boulders round the fall and the pools, logs, stumps and dry branches placed near the path by hashed search; a collision box per mesh part (`SurfaceBody3D`, tagged `rock` / `wood`) |
| Edges | four 200 m high `StaticBody3D` walls just outside the map |
| Player | `ForestValley` stands the player on the ground and sets `SurfaceResolver` to `Terrain3D.SurfaceTagAt` (needles sound as leaves); `ForestAudio` gets the pine density for the woodpecker |

Trees are generated at load from the Ez Tree presets (14 variants, ≈ 60 ms), not baked: a baked `TreeMesh` `.mres` is
≈ 3 MB of text per variant (≈ 45 MB for the forest) to save that time.

**Pre-warm.** For the first frames (`PrewarmFrames`, 8, from the second frame, after the foliage built its tiles) every
terrain level, foliage tile, tree level and prop is shown with no visibility range, every multimesh draws all its
instances and the foliage's thinning is paused; then each gets back what LOD, distance and thinning chose. So every GPU
buffer, material set and pipeline exists before play: without it the walk allocated (and hitched) the first time
something came into view.

### The look (`ForestScene.CreateEnvironment`, the `Sun`, `Content/PostProcess/*.mres`)

The post-processing look is a resource file the editor tunes ([ADR 0169](../../memory/decisions/0169-post-processing-profile-and-editor-preview.md)):
`Content/PostProcess/forest.mres` (a `PostProcessProfile`: exposure, glow, shafts, SSAO, the grade) and
`Content/PostProcess/forest-lens.mres` (the `CameraAttributesPractical`), referenced by the scene's `Environment`
(`ForestLook`). **The files are the source of truth**: `--write-scenes` writes them from `ForestLook.CreateProfile` and
`ForestGrade.CreateLens` only when they are missing, so values tweaked in the editor survive a scene rebuild; the table
holds their first values. To tune it: `just editor Examples/Forest/project.mfproj` opens the forest scene with
**Preview Post-Processing** on (the valley is generated in the editor too: `ForestValley` is a `[Tool]`); select
`Environment`, Edit (pencil) on `Post Process`, open a group, change a value: the view follows at once, and Save writes
`forest.mres` with the scene. `Tests/QA/forest-post.qa` drives that and captures it.

| Knob | Value | Why |
|---|---|---|
| Sun | `DirectionalLight3D`, colour (1, 0.87, 0.7), energy 2.6, 4 cascades × 1024² to 140 m, split λ 0.8; G8e.2 ([ADR 0167](../../memory/decisions/0167-shadow-quality-staggered-pcss-contact-far.md)): `ShadowCacheMode.Staggered`, `ShadowCoarseCascades` 2, `LightAngularDistance` 0.5°, `ContactShadows` (0.4 m), `FarShadowEnabled` | warm and low: long soft shadows across the glade and the pond, ferns and rocks grounded; the shadow budget below |
| Sky | `Physical`, turbidity 6, Mie 0.005, ground (0.28, 0.27, 0.2) | a clear morning; the IBL follows it |
| Ambient / reflections | `AmbientSource.Sky` × 1.6, `ReflectedLightSource.Sky` | no GI: the sky fills the shade so it stays readable next to the sun |
| Fog | density 0.005, height 6 m, height density 0.08, colour (0.42, 0.47, 0.53), sun scatter 0.1 | haze in the valley beyond 64 m; the volumetric fog does the near air |
| Volumetric fog ([ADR 0171](../../memory/decisions/0171-volumetric-fog.md)) | density 0.018, anisotropy 0.75, length 64 m, sky affect 0.2, ambient inject 0.02, noise 0.5 at 8 m; temporal reprojection 0.9 | soft rays through the canopy gaps from any view (also with the sun off screen, between the trunks of R3 and R5), the shaded air clear, hazier towards the valley floor (the height fog's shape) |
| Light shafts | off (intensity 2.3, decay 0.965, density 0.85 kept) | the volumetric fog draws real shafts; the screen-space ones on top made the glade milky |
| Exposure | auto, scale 0.4, speed 0.6; engine ACES | adapts between the glade and the pine shade |
| Glow | intensity 0.3, threshold 4, luminance cap 3, no bloom | only the sun and its glints bloom |
| Wind | from the east, strength 0.35, 0.45 Hz, turbulence 0.4 | a breeze |
| Anti-aliasing | `rendering.antiAliasing: Taa` (ADR 0166), sharpness 0.25 (default); tree leaves and ferns `AlphaDither` | leaf, needle and grass edges stop shimmering in motion; the stream is a reactive pixel (keeps 0.2 of its history) so its flow does not smear |
| Grade (ADR 0168) | `AdjustmentEnabled`, `AdjustmentColorCorrection` = `Content/Grading/forest-morning.cube` (33³, `ForestGrade`, written by `--write-scenes`), strength 1 | a warm, soft morning: warm white balance that spares the sky, a soft curve with lifted blacks and a gentle shoulder, cool shade and golden light, olive-yellow foliage, saturation up in the mid-tones and down in the highlights |
| Lens (ADR 0168) | `CameraAttributes`: vignette 0.2, grain 0.015 (1.5 px), no aberration, no depth of field | a filmic frame without a muddy one; depth of field only in the photo shots |
| Water (G8e.6, [ADR 0173](../../memory/decisions/0173-water-scene-copy-refraction-ssr-falls.md)) | both refract (`RefractionEnabled`) with caustics and screen-space reflections (`rendering.waterSsr` High). Stream: absorption (0.42, 0.13, 0.1), roughness 0.05, reflection 0.7, normals 1.2/5 m × 0.6, caustics 0.9 every 1.6 m to 1.2 m deep, shore foam 8 cm. Pond: peaty absorption (2.4, 1.4, 1.2), reflection 1, calm (normals × 0.3), caustics 0.6 every 2.5 m. The 3.5 m fall is a `River3D` fall: a jet (`WaterfallMaterial3D`) with spray cards at its foot | the stream shows its gravel bed bent by the ripples, with caustics; the pond's shallows show the bed, its middle goes dark and mirrors the trees and banks; the fall is a white sheet with mist |

`++ --set Node/Path.Property=value` overrides any of these for a run (`Valley.*` before the valley generates), e.g.
`--set Sun.ShadowCascades=3 --set Environment.Sky.Turbidity=10 --set Valley.GroundCoverDensity=1.2`.

### Reference shots

`++ --shot <1-5|name>` puts a fixed camera at a pose of `ValleyLayout.Shots` (heights above the ground); `--view
x,y,z,tx,ty,tz[,fov]` at any pose (y 0: eye height above the ground, −h: h above it). `just forest-screenshots` renders
all five at 2560 × 1440 (frame 300 at a fixed 60 Hz: fixed wind, water and exposure) and scales them to 1600 × 900 into
`docs/images/forest/`. R4 and R5 are photo shots (`ReferenceShot.BlurNearUntil`/`BlurFarFrom`, `ForestDev.PhotoLens`):
their camera adds subtle 32-tap depth of field to the Forest's lens (R4: what is nearer than 6 m blurs; R5: the trees
behind the ferns blur from 9 m); walking never has depth of field:

| | |
|---|---|
| ![R1](../images/forest/r1-glade.png)<br>**R1 Glade**: into the low sun through oaks and ashes; shafts, haze, back-lit leaves | ![R2](../images/forest/r2-fall.png)<br>**R2 The fall**: from the falls viewpoint, the 3.5 m drop between boulders, pool 1, dappled moss |
| ![R3](../images/forest/r3-bridge.png)<br>**R3 Log bridge**: the west bank; the gravel bed through clear water, mud and moss banks, the log | ![R4](../images/forest/r4-vista.png)<br>**R4 Vista**: from the pine slope's lookout down to the pond |
| ![R5](../images/forest/r5-floor.png)<br>**R5 Forest floor**: 0.5 m above leaf litter and ferns among pines and aspens | |

**Volumetric fog (G8e.3, [ADR 0171](../../memory/decisions/0171-volumetric-fog.md)).** The shots above are with it. Against
the screen-space shafts it replaced: R1 keeps rays through the canopy around the sun but the trees to either side are no
longer veiled (the old milky haze was the shafts' radial blur and bloom plus the height fog's sun scatter; with the
shafts simply off the glade was already clear); R3 and R5 show shafts between the trunks with the sun off screen, and
the shade under the canopy got darker (mean luminance R3 59 → 39, R1 122 → 109) because nothing brightens the shaded
air any more; R4's valley hazes over from the far fog (0.005). Looking 60° away from the sun (`--view
48,-1.7,146,87.5,-15,99.5`) the forward-scattering fog shows a warm glow and faint rays at the frame's edge: with one
Henyey–Greenstein lobe (g 0.75) off-axis shafts stay subtle unless the fog is denser, which made R1 milky again.

Before/after pairs of the G8e phases are in [`docs/images/forest/g8e/`](../images/forest/g8e/) (G8e.6, water:
`g8e6-r2-fall`, `g8e6-r3-bridge`, `g8e6-r4-vista`, each `-before` and `-after`; the after shots and R2–R4 above were
rendered at 2304 × 1296, the display's limit at the time, and scaled to 1600 × 900 like the rest).

**What limits the look** (engine features the slice does not have yet): no GI (the sky's IBL fills the shade, so
interiors of the canopy read flat), TAA softens the image a little in motion (the sharpen restores some of it), no
impostors or coverage-preserving alpha mips (distant canopies read
as speckled cards), reflections only of what is on screen (water elsewhere reflects the sky, too bright under the
canopy until G8e.1's probes dim it), spray as a few soft cards (particles are G6.3), and 1024² rather than 2048² shadow
cascades (2048² costs 4–6 ms more under the leaf cards until G8e.5's impostors).

### Performance

Apple M5 (MoltenVK), Release, `just forest-bench` (1 800 frames along the spline, VSync off; other lanes' GPU work was
running at times):

| Resolution | p50 | p90 | p99 | Sun shadows (GPU p50) |
|---|---|---|---|---|
| 1600 × 900 | 11.0 ms | 13.7 ms | 16.2 ms | 2.0 ms |
| **1920 × 1080** | **13.0 ms** | **16.0 ms** | **20.2 ms** | 2.0 ms |
| 2560 × 1440 | 18.4 ms | 23.4 ms | 27.3 ms | 2.1 ms |

Measured before SSAO (ADR 0165), which turns the depth prepass on: at 1920 × 1080 the passes take ≈ 1 ms of GPU time
and the prepass saves about as much, so the frame stays near these numbers (ADR 0165 has the runs). The cinematic grade
(ADR 0168: LUT, vignette, grain, one `AfterTonemap` pass) costs ≈ 0.16 ms GPU at 1080p. ≤ 465 draws, 0 B per frame,
≈ 4 s from launch to the first measured frame. 1080p holds 60 fps at the median and through
most of the flight; the p99 frames are the vista over the whole valley. Measured at 1080p against these defaults: the
engine's default sun shadows (4 × 2048² to 100 m) +12 ms, 3 × 2048² to 90 m +8 ms, 3 × 1024² to 110 m +1.2 ms;
hex-tiling +3.5 ms; the trees +10 ms; the ground cover +5 ms (p50). The GPU, not the CPU, bounds every case.

**G8e.2 shadows** ([ADR 0167](../../memory/decisions/0167-shadow-quality-staggered-pcss-contact-far.md)), `just forest-bench`
at 1920 × 1080, interleaved runs on a busy machine (other lanes' GPU and Docker work), the old sun (2 × 1024² to 60 m)
against the new one with the same build:

| Sun | p50 | p90 | p99 | Sun shadows (GPU p50) |
|---|---|---|---|---|
| Before: 2 × 1024² to 60 m, every frame | 15.37 / 15.46 / 15.44 ms | 19.3–20.7 ms | 24.2 / 25.7 / 25.3 ms | 2.1 ms |
| **After:** 4 × 1024² to 140 m staggered, coarse level 1, PCSS 0.5°, contact shadows, far shadow | **14.11 / 14.20 / 15.40 ms** | 19.5–21.6 ms | **25.0 / 23.9 / 28.8 ms** | 3.7–4.0 ms |

The frame stays within noise of before: the contact shadows turn the depth prepass on, which pays for itself (ADR 0163),
and that offsets PCSS in the lit pass and the larger shadow pass (still two 1024² cascade passes a frame, but
over 140 m and with the far trees). Measured along the way: the same setup every frame 15 ms of shadows; 4 × 2048²
staggered +4–6 ms of shadows; the first PCSS (16 point taps, no early-outs) +3 ms; Ez Tree's level 2 as the coarse level
+2–3 ms and a black forest floor; translation-only cascade margins 2.7 cascade passes a frame (turning). At the start of
the lane, on a quieter machine, the old setup measured p50 13.0 ms, p99 20.25 ms, shadows 2.0 ms.

**G8e.3 volumetric fog** ([ADR 0171](../../memory/decisions/0171-volumetric-fog.md)), `just forest-bench` at 1920 × 1080,
interleaved runs on a busy machine (other lanes' GPU and Docker work: the sun shadows read 3.6–5.0 ms here against 2.0 ms
quiet), the old look (screen-space shafts, sun scatter 0.3, no volumetrics) against the new with the same build:

| Look | p50 | p90 | p99 | Volumetric fog (GPU p50) |
|---|---|---|---|---|
| Before: screen-space shafts | 19.35 / 16.70 ms | 30.2 / 21.3 ms | 36.3 / 30.3 ms | — |
| **After:** volumetric fog, shafts off | **18.26 / 16.72 ms** | 37.2 / 21.3 ms | 46.5 / 31.4 ms | **1.41 / 1.27 ms** |

The quieter pair (the second) is equal at the median and 1 ms apart at p99: the fog's passes replace the shafts' three,
and the difference stays inside this machine's run-to-run noise (the first pair's spread is larger than either change).
A quiet-machine baseline is still to be recorded (`--write-baseline`). 0 B per frame.

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
| `StreamAudio` | the **brook** (14 s mono: band-passed noise at 800 Hz and 2.5 kHz that undulates, a low rumble, ≈ 38 Minnaert bubbles per second — sines at 350 Hz – 2.4 kHz whose pitch rises 1.3–2.4× as they decay — and bursts of babble) on two `AudioPlayer3D`s: the near one at the river point closest to the listener (`Curve3D.GetClosestOffset`), pushed toward the listener by up to half the width; the far one 12 m further along, −5 dB, half a loop apart. Both move at most 30 m/s. Level `StreamVolumeDb + clamp(20·log10(flow / 1 m/s), −6, +6)` from `River3D.FlowSpeedAt`; `UnitSize` 3 m, `MaxDistance` 30 m, low-pass to 2.5 kHz at range. The **falls** loop (10 s: the brook's recipe, brighter and denser) plays at each of `FallPositions` (`MaxDistance` 80 m); the positions are parameters (`River3D.Falls` now lists the rendered falls, ADR 0173), and `StreamAudio.FindSteepPoints(river, minSlope 0.25, spacing 15 m)` finds candidates | Water |
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

## `ForestDev`, `--autowalk` and the benchmark

The `Dev` autoload (`ForestDev`) is idle unless started with game arguments after `++`:

- `--autowalk`: `PathWalker` walks the loop from the trailhead (it presses `move_forward` and turns towards a point 4 m
  ahead on the path, sprints 10 s of every 40 and jumps once), then measures a 600-frame window after `--warmup` frames
  (default 1 500: past the first jump, landing and sprint, so the physics' working lists have grown) with
  `GC.GetAllocatedBytesForCurrentThread` on the main thread (the whole frame: tree, physics, rendering, UI, audio) and
  quits with exit code 0 when it allocated nothing, 1 otherwise, listing the frames that did. Measured: **0 B**, 50
  footsteps, 0 gen-0 collections.
- `--autowalk-lap`: walks the whole loop and quits when back at the trailhead (exit 1 if stuck after 12 minutes).
  Measured: 150 s, 469 m, 98 % of frames on the floor, 618 footsteps; the bridge and every slope walkable.
- `--benchmark [--frames n] [--out file.json] [--baseline file.json [--write-baseline]]`: `ForestBenchmark` waits 240
  frames, then a camera flies the 15-point `ForestBenchmark.Spline` (low through the glade, past the fall, over the pine
  canopy, down to the pond and back to the trailhead) for n frames (default 1 800); it prints p50/p90/p99/p99.9/max of
  the wall-clock frame intervals, the sun shadows', SSAO's and the volumetric fog's GPU time and the most draws, writes JSON and compares p50 and p99 with
  the baseline (exit 1 when more than 10 % slower or when a frame allocated). `just forest-bench` runs it at
  1920 × 1080 against `Examples/Forest/benchmark-baseline.json`.
- `--shot`, `--view`, `--set`: [above](#reference-shots); `--resolution WxH` resizes the window to W × H pixels.
- `--no-capture` leaves the mouse free; `--no-audio` skips the audio `ForestDev` would attach to a scene without one.
- **Frame rate:** `ForestDev` adds `FpsHud` (`Src/Dev/FpsHud.cs`, `Content/UI/fps.rml`) on its own `UiLayer` (100) in normal
  play: FPS, the average frame time and the slowest frame of the last half second, top right; F3 toggles it. Screenshot,
  benchmark and autowalk runs leave it out (clean captures, allocation windows), and so does `--no-fps`. The values are
  published as numbers twice a second (RmlUi formats them), so it allocates nothing per frame.

The controller has two fixes for the terrain: on 0.5 m triangles it lost the floor on 46 % of frames (walking up one
triangle's plane leaves it above the next, flatter one, and `MoveAndSlide` does not snap a body moving up), so a ray
within `FloorSnapLength` now snaps it back; and touching down is a footstep only after `LandingAirTime` (0.2 s) in the
air. Jitter2's "EPA could not converge" warning, which character queries on the terrain hit every few seconds and whose
string Jitter2 allocates, is muted for those queries (`JitterLogMute`, [Physics](physics.md)).

## Recipes

| Recipe | What |
|---|---|
| `just forest *args` | `dotnet run --project Examples/Forest/Forest.Desktop -c Release -- {{args}}` |
| `just forest-test` | `dotnet test Examples/Forest/Forest.Tests` |
| `just forest-audio` | the offline audio renders: `Examples/Forest/artifacts/audio/*.wav` (ambience calm and windy, brook near and far, falls, birds, footsteps per surface, the whole mix walking) |
| `just forest-screenshots [frames]` | `build/forest-screenshots.sh`: R1–R5 at 2560 × 1440 → 1600 × 900 into `docs/images/forest/` (display awake, LFS art) |
| `just forest-bench [args]` | `build/forest-bench.sh`: the benchmark at 1920 × 1080 against `benchmark-baseline.json` (`--write-baseline` re-records; quiet machine) |

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
- settings round trip; the project file (main scene, keyboard and pad bindings, TAA, pixel size); the scene contract
  (sun first, the look's switches, the valley, the audio wiring, a low east-south-east sun); `PathWalker` drives the walk;
- the valley (`ValleyTests`): the same seed gives bit-identical heights, splat weights and tree placements and another
  seed different ones; the stream falls from the outcrop over the fall to the pond and the ridges stand above the floor;
  the path crosses the stream exactly once, at the bridge, above the water; the loop is a 2.5–5 minute walk and no point
  of the path is steeper than 35°; trees keep clear of the path, the water, the bridge and the clearings, and every zone
  has its species; a headless build (no art) stands the player on the ground at the trailhead, on the floor after a
  second, with water in pool 1, the pond at its level, the bridge's walkway above the stream and 1 200–2 600 trees;
  footstep tags; the shots and the benchmark spline on the map;
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
