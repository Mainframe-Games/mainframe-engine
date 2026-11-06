using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The renderer's post-processing side as the render server drives it (ADR 0163): the main view's
/// <see cref="PostProcessStack"/>, the depth prepass and the <see cref="PostStage.AfterPrepass"/> stage. The
/// <see cref="PostStage.BeforeTonemap"/> and <see cref="PostStage.AfterTonemap"/> stages run inside
/// <see cref="IVulkanContext.BeginOverlayPass"/>. Implemented by the Vulkan renderer.
/// </summary>
internal interface IPostProcessHost
{
    /// <summary>
    /// The exposure the main view's tonemap applies: the frame's (manual or the Godot tonemaps'), times auto exposure's
    /// factor as last read back (ADR 0257: <c>IgnoreExposure</c> surfaces divide by it).
    /// </summary>
    float EffectiveExposure { get; }

    /// <summary>The main view's post effects (built-ins registered at start-up).</summary>
    PostProcessStack PostEffects { get; }

    /// <summary>A debug view replacing the final image (<see cref="RenderServer.DebugView"/>).</summary>
    RenderDebugView DebugView { get; set; }

    /// <summary>What the effects decide on this frame: <see cref="IVulkanContext.PostProcess"/>, anti-aliasing, debug view.</summary>
    PostEffectSettings PostSettings { get; }

    /// <summary>The root world's primary light's contact shadows (ADR 0167), set by the render server each frame.</summary>
    ContactShadowSettings ContactShadows { get; set; }

    /// <summary>The root world's volumetric fog (ADR 0171), set by the render server each frame.</summary>
    VolumetricFogSettings VolumetricFog { get; set; }

    /// <summary>The shadow set the main view's lit shaders bind (null: none), set by the render server each frame.</summary>
    IShadowDescriptors? ShadowDescriptors { get; set; }

    /// <summary>The depth prepass's render pass (the prepass is created on first use): build prepass pipelines against it.</summary>
    RenderPass PrepassRenderPass { get; }

    /// <summary>The main view's camera this frame (null: none), for <see cref="PostEffectContext.Camera"/>.</summary>
    void SetMainCamera(ICamera? camera);

    /// <summary>
    /// Starts the frame's post-processing, before any pass that binds the main view's frame set: clears the ambient
    /// occlusion and contact shadow bindings, then every enabled effect's <c>OnBeginFrame</c> (creating it first). <paramref name="prepass"/>:
    /// this frame runs the depth prepass (it is created now, so effects see its images).
    /// </summary>
    void BeginPostFrame(bool prepass);

    /// <summary>Begins the depth prepass (no render pass active; the frame set's view 0 written).</summary>
    void BeginPrepass();

    /// <summary>Draws the sky's velocity and ends the prepass: this frame's scene pass loads its depth.</summary>
    void EndPrepass();

    /// <summary>Records the <see cref="PostStage.AfterPrepass"/> stage (SSAO), after <see cref="EndPrepass"/>.</summary>
    void RecordAfterPrepass();
}

/// <summary>The exposure a view's tonemap applies, for surfaces that cancel it (ADR 0257).</summary>
internal static class PostExposure
{
    /// <summary>
    /// <paramref name="frameExposure"/> times auto exposure's <see cref="PostProcessSettings.AutoExposureFor"/> of
    /// <paramref name="adaptedLuminance"/> (the last value read back; 0 = none yet), when the post tonemap runs with auto
    /// exposure.
    /// </summary>
    public static float Effective(float frameExposure, in PostProcessSettings settings, bool postTonemap, float adaptedLuminance)
    {
        if (!postTonemap || !settings.AutoExposureEnabled || !(adaptedLuminance > 0f))
            return frameExposure;
        return frameExposure * settings.AutoExposureFor(adaptedLuminance);
    }
}
