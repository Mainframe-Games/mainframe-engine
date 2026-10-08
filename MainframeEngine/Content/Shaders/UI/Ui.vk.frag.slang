// RmlUi geometry: vertex colour × texture, both premultiplied and sRGB-encoded (the UI is authored in sRGB and blended
// in sRGB space, like a browser). Untextured geometry binds a 1×1 white texture. Engine textures (engine://) may be
// stored linear (sRGB formats, HDR targets) or with straight alpha; textureFlags converts them on the fly.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D uiTexture;

struct UiPush
{
    [[vk::offset(72)]] uint textureFlags;
};

[[vk::push_constant]] ConstantBuffer<UiPush> pc;

static const uint kEncodeSrgb = 1u;  // the sampler returns linear colour: encode to sRGB
static const uint kPremultiply = 2u; // the texture stores straight alpha
static const uint kDepthToGray = 4u; // a depth map: .r as grey, alpha 1

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord, [[vk::location(1)]] float4 inColor) : SV_Target0
{
    float4 texel = uiTexture.Sample(inTexCoord);
    if ((pc.textureFlags & kDepthToGray) != 0u)
        texel = float4(texel.rrr, 1.0);
    if ((pc.textureFlags & kEncodeSrgb) != 0u)
        texel.rgb = linearToSrgb(clamp(texel.rgb, 0.0, 1.0));
    if ((pc.textureFlags & kPremultiply) != 0u)
        texel.rgb *= texel.a;
    return inColor * texel;
}
