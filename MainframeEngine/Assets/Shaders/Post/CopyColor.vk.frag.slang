// A fullscreen copy of a scene-sized HDR image into the scene colour (ADR 0163: PostEffectContext.CopyToSceneColor), one
// texel per pixel. Paired with Fullscreen.vk.vert.
[[vk::binding(0, 0)]] Sampler2D source;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    return source.Load(int3(int2(fragCoord.xy), 0));
}
