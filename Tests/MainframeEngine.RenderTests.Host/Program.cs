using System.Text.Json;
using MainframeEngine;
using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

// Content paths resolve against the output folder (ContentPaths, AssetDatabase.Current), not the working directory.
var options = HostOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
if (options.PipelineCacheDirectory is not null)
    Environment.SetEnvironmentVariable(PipelineCache.DirectoryVariable, options.PipelineCacheDirectory);

if (options.Scene == "gamehost")
{
    var (gameHostExit, gameHostResult) = GameHostRun.Run(options);
    return WriteResult(gameHostResult, gameHostExit);
}

RenderTestGame game = options.Scene switch
{
    "lit-shapes" => new LitShapesScene(options),
    "multi-light" => new MultiLightScene(options),
    "mouse-look" => new MouseLookScene(options),
    "spine" => new SpineScene(options),
    "spine-no-shadows" => new SpineNoShadowsScene(options),
    "spine-small-scale" => new SpineSmallScaleScene(options),
    "spine-2d" => new Spine2DScene(options),
    "showcase" => new ShowcaseScene(options),
    "color-pipeline" => new ColorPipelineScene(options),
    "physics" => new PhysicsScene(options),
    "physics-debug" => new PhysicsDebugScene(options),
    "sky-grid" => new SkyGridScene(options),
    "materials" => new MaterialsScene(options),
    "outline" => new OutlineScene(options),
    "subviewport-capture" => new SubViewportCaptureScene(options),
    "subviewport-post" => new SubViewportPostScene(options),
    "gltf" => new GltfScene(options),
    "instances" => new InstancesScene(options),
    "picking" => new PickingScene(options),
    "ui-hud" => new UiHudScene(options),
    "canvas" => new CanvasScene(options),
    "ui-effects" => new UiDocumentScene(options, "Content/UI/effects.rml"),
    "ui-widgets" => new UiDocumentScene(options, "Content/UI/widgets/demo.rml"),
    "ui-text" => new UiDocumentScene(options, "Content/UI/text.rml"),
    "ui-region" => new UiRegionScene(options),
    "ui-preload" => new UiPreloadScene(options),
    "csm" => new CsmScene(options),
    "shadow-pcf" => new ShadowPcfScene(options),
    "shadow-opacity" => new ShadowOpacityScene(options),
    "glow" => new GlowScene(options),
    "shadow-lights" => new ShadowLightsScene(options),
    "shadow-cutout" => new ShadowCutoutScene(options),
    "shadow-shimmer" => new ShadowShimmerScene(options),
    "shadow-pcss" => new ShadowPcssScene(options),
    "contact-shadows" => new ContactShadowsScene(options),
    "shadow-staggered" => new ShadowStaggeredScene(options),
    "far-shadow" => new FarShadowScene(options),
    "sky-physical" => new PhysicalSkyScene(options),
    "fxaa" => new FxaaScene(options),
    "auto-exposure" => new AutoExposureScene(options),
    "pbr" => new PbrSpheresScene(options),
    "fog" => new FogScene(options),
    "vertex-colors" => new VertexColorsScene(options),
    "multimesh" => new MultiMeshScene(options),
    "foliage-wind" => new FoliageWindScene(options),
    "terrain" => new TerrainScene(options),
    "water" => new WaterScene(options),
    "terrain-splat" => new TerrainSplatScene(options),
    "terrain-foliage" => new TerrainFoliageScene(options),
    "light-shafts" => new LightShaftsScene(options),
    "volumetric-fog" => new VolumetricFogScene(options),
    "velocity" => new VelocityScene(options),
    "post-copy" => new PostCopyScene(options),
    "taa-edges" => new TaaEdgesScene(options),
    "taa-ghost" => new TaaGhostScene(options),
    "taa-foliage" => new FoliageWindScene(options with { AntiAliasing = options.AntiAliasing ?? AntiAliasing.Taa }),
    "ssao" => new SsaoScene(options),
    "post-grade" => new PostGradeScene(options),
    "post-dof" => new PostDofScene(options),
    "post-film" => new PostFilmScene(options),
    "tree-realistic" => new TreeScene(options, MainframeEngine.Trees.TreeStyle.Realistic),
    "tree-lowpoly" => new TreeScene(options, MainframeEngine.Trees.TreeStyle.LowPoly),
    "tree-forest" => new TreeForestScene(options),
    _ => throw new ArgumentException(
        $"Unknown scene '{options.Scene}'. Known: lit-shapes, multi-light, mouse-look, spine, spine-no-shadows, spine-small-scale, spine-2d, showcase, color-pipeline, " +
        "physics, physics-debug, sky-grid, materials, outline, subviewport-capture, gltf, instances, picking, ui-hud, ui-effects, ui-widgets, ui-text, ui-region, ui-preload, " +
        "csm, shadow-pcf, shadow-opacity, glow, shadow-lights, shadow-cutout, shadow-shimmer, shadow-pcss, contact-shadows, shadow-staggered, far-shadow, " +
        "sky-physical, fxaa, auto-exposure, pbr, fog, vertex-colors, multimesh, " +
        "foliage-wind, terrain, water, terrain-splat, terrain-foliage, light-shafts, volumetric-fog, velocity, post-copy, taa-edges, taa-ghost, taa-foliage, ssao, " +
        "post-grade, post-dof, post-film, tree-realistic, tree-lowpoly, tree-forest, gamehost."),
};

using (game)
{
    var exit = game.Run();
    return WriteResult(game.Result, exit);
}

int WriteResult(HostResult? result, ExitCode exit)
{
    if (result is null)
    {
        Console.Error.WriteLine($"Scene '{options.Scene}' did not complete (exit {exit}).");
        return 2;
    }

    // The result is written whatever the exit code, so tests can check Quit(ExitCode.Error) too.
    var json = JsonSerializer.Serialize(result with { ExitCode = (int)exit }, HostResult.JsonOptions);
    File.WriteAllText(Path.Combine(options.OutputDirectory, HostResult.FileName), json);
    return (int)exit;
}
