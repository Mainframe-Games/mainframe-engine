// SpineRenderer: set 0 frame (camera + lights), set 1 shadows, set 2 atlas page texture.
// Output is premultiplied (blend One, OneMinusSrcAlpha); premultiplied alpha is handled here, once.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"

// True for premultiplied-alpha atlases (atlas page "pma: true"), which are bound as UNORM images.
[vk::constant_id(0)] const bool kPremultipliedTexture = false;

[[vk::binding(0, 2)]] Sampler2D uTexture;

struct FsIn
{
    [[vk::location(0)]] float2 inUV;
    [[vk::location(1)]] float4 inColor;   // straight, sRGB-authored slot/skeleton tint
    [[vk::location(2)]] float3 inWorldPos;
    [[vk::location(3)]] float3 inNormal;
};

[shader("fragment")]
float4 main(FsIn input) : SV_Target0
{
    float4 tex = uTexture.Sample(input.inUV);
    if (kPremultipliedTexture)
    {
        // Multiplied in sRGB space by the exporter: un-premultiply, decode, re-premultiply in linear.
        tex.rgb = tex.a > 0.0 ? srgbToLinear(tex.rgb / tex.a) * tex.a : float3(0.0);
    }
    else
    {
        tex.rgb *= tex.a; // sRGB image, already decoded by the sampler; straight alpha → premultiplied
    }

    float alpha = tex.a * input.inColor.a;
    if (alpha < 0.01) discard;

    float3 base = tex.rgb * srgbToLinear(input.inColor.rgb) * input.inColor.a; // premultiplied linear albedo
    float3 N = normalize(input.inNormal);
    return float4(shadeLights(base, N, input.inWorldPos), alpha);
}
