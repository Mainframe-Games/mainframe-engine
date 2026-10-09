using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Water that reads the scene behind it (ADR 0173): the water-scene pipeline layout (sets 0-2 shared with the mesh
/// layout, set 3 = the view's <see cref="WaterSceneTextures"/> copy), its fallback set 3 (a white 1×1, for spray cards in
/// views without a copy), and the push block the water-scene shaders read (<c>include/water_scene.slang</c>).
/// </summary>
internal sealed unsafe partial class MeshRenderer
{
    private DescriptorSetLayout _sceneSetLayout;
    private PipelineLayout _waterScenePipelineLayout;
    private DescriptorPool _sceneFallbackPool;
    private DescriptorSet _sceneFallbackSet;

    /// <summary>The TAA reactivity refracting water writes (the scene alpha becomes 1 − this): ripples and SSR keep some history.</summary>
    internal const float WaterSceneReactivity = 0.85f;

    /// <summary>Set 3 of the water-scene pipelines: one combined image sampler (the copy).</summary>
    internal DescriptorSetLayout SceneSetLayout => _sceneSetLayout;

    /// <summary>Screen-space reflection quality of refracting water (<see cref="RenderServer.WaterSsr"/>).</summary>
    internal WaterSsrQuality WaterSsr { get; set; } = WaterSsrQuality.Low;

    private void CreateWaterSceneResources()
    {
        _sceneSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "water scene set 3");
        _waterScenePipelineLayout = _ctx.Frame.CreatePipelineLayout(_shadowDescriptors, [_materialLayout, _sceneSetLayout], "mesh (water scene)");
        _sceneFallbackPool = PipelineBuilder.CreatePool(_ctx, 1,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "water scene fallback");
        _sceneFallbackSet = PipelineBuilder.AllocateSet(_ctx, _sceneFallbackPool, _sceneSetLayout, "water scene fallback");
        PipelineBuilder.WriteImage(_ctx, _sceneFallbackSet, 0, new DescriptorImageInfo
        {
            Sampler = _whiteLinear.Sampler,
            ImageView = _whiteLinear.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
    }

    private void DisposeWaterSceneResources()
    {
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_sceneFallbackPool));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_waterScenePipelineLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_sceneSetLayout));
    }

    /// <summary>True for shader sets drawn with the water-scene pipeline layout.</summary>
    internal static bool UsesSceneLayout(ShaderSetId shaders) => shaders is ShaderSetId.MeshWaterScene or ShaderSetId.MeshSpray;

    /// <summary>
    /// Binds set 3 (the view's copy, or the fallback) and pushes the material's block for a water-scene draw run. Set 3 is
    /// bound again with every such material: binding set 2 with the mesh layout (which has no set 3) disturbs it.
    /// </summary>
    private void BindWaterScene(CommandBuffer cb, MeshViewDraws view, MaterialGpu material)
    {
        var vk = _ctx.Vk;
        var set = view.Scene?.WaterSet ?? _sceneFallbackSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _waterScenePipelineLayout, 3, 1, &set, 0, null);

        var push = WaterScenePush.For(material.Material, view.Scene, WaterSsr);
        vk.CmdPushConstants(cb, _waterScenePipelineLayout, FrameContext.PushConstantStages, 0, WaterScenePush.Size, &push);
    }

    /// <summary>The push block of <c>include/water_scene.slang</c> (64 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaterScenePush
    {
        public Vector4 Refraction; // strength, blur, the copy's last level, 1 with the view's copy
        public Vector4 Caustics;   // 1 / scale, strength, max depth, 1 for SSR
        public Vector4 Ssr;        // steps, refinement steps, max distance, thickness
        public Vector4 Extra;      // TAA reactivity

        public const uint Size = 64;

        /// <summary>Steps, refinement steps, max distance (m) and thickness (m) of a quality (Off: no march).</summary>
        public static Vector4 SsrSettings(WaterSsrQuality quality) => quality switch
        {
            WaterSsrQuality.High => new Vector4(28f, 6f, 60f, 0.35f),
            WaterSsrQuality.Low => new Vector4(16f, 4f, 40f, 0.5f),
            _ => Vector4.Zero,
        };

        public static WaterScenePush For(Material material, WaterSceneTextures? scene, WaterSsrQuality quality)
        {
            var push = new WaterScenePush
            {
                Refraction = new Vector4(0f, 0f, scene is null ? 0f : scene.MipCount - 1, scene is null ? 0f : 1f),
                Ssr = SsrSettings(quality),
                Extra = new Vector4(WaterSceneReactivity, 0f, 0f, 0f),
            };
            if (material is WaterMaterial3D water)
            {
                push.Refraction.X = MathF.Max(water.RefractionStrength, 0f);
                push.Refraction.Y = Math.Clamp(water.RefractionRoughness, 0f, 1f);
                push.Caustics = new Vector4(1f / MathF.Max(water.CausticsScale, 0.01f), MathF.Max(water.CausticsStrength, 0f),
                    MathF.Max(water.CausticsMaxDepth, 0.01f), water.ScreenSpaceReflections && quality != WaterSsrQuality.Off ? 1f : 0f);
            }

            return push;
        }
    }
}
