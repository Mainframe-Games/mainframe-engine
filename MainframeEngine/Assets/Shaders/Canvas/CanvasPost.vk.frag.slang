// 2D canvas post-processing (ADR 0185): the post-processed canvas layers (premultiplied, gamma space) + glow, then the
// colour grade (CanvasPostMath.Grade: brightness, contrast, saturation, lift/gamma/gain, tint), the vignette, chromatic
// aberration and grain, written premultiplied for the layers drawn on top and the composite.
[[vk::binding(0, 0)]] Sampler2D scene; // the canvas layer (bilinear, clamp to edge)
[[vk::binding(0, 1)]] Sampler2D glow;  // glow level 0 (half size), or the white texture when flags & 1 is clear

struct Post
{
    float4 lift;      // rgb, w: brightness
    float4 gamma;     // rgb, w: contrast
    float4 gain;      // rgb, w: saturation
    float4 tint;      // rgb, a: strength
    float4 vignette;  // rgb: colour, a: strength
    float4 shape;     // x: vignette radius, y: vignette softness, z: grain amount, w: grain size
    float4 misc;      // x: chromatic aberration, y: glow intensity, z: grain seed, w: flags (1: glow) as uint bits
    float2 dstSize;   // target size in pixels
};

[[vk::push_constant]] ConstantBuffer<Post> pc;

static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

float3 grade(float3 rgb)
{
    rgb *= pc.lift.w;
    rgb = lerp(float3(0.5), rgb, pc.gamma.w);
    rgb = lerp(float3(dot(rgb, kLuma)), rgb, pc.gain.w);
    rgb = max(rgb * pc.gain.rgb + pc.lift.rgb * (float3(1.0) - rgb), float3(0.0));
    rgb = pow(rgb, float3(1.0) / pc.gamma.rgb);
    rgb *= lerp(float3(1.0), pc.tint.rgb, pc.tint.a);
    return rgb;
}

float hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = fragCoord.xy / pc.dstSize;
    float2 centred = uv - 0.5;

    float4 c = scene.SampleLevel(uv, 0.0);
    if (pc.misc.x > 0.0)
    {
        float2 shift = centred * 2.0 * pc.misc.x;
        c.r = scene.SampleLevel(uv + shift, 0.0).r;
        c.b = scene.SampleLevel(uv - shift, 0.0).b;
    }

    uint flags = asuint(pc.misc.w);
    if ((flags & 1u) != 0u)
    {
        float3 g = glow.SampleLevel(uv, 0.0).rgb * pc.misc.y;
        c.rgb += g;
        c.a = saturate(max(c.a, max(g.r, max(g.g, g.b))));
    }

    if (c.a <= 0.0)
        return float4(0.0);

    float3 rgb = grade(c.rgb / c.a);

    float aspect = pc.dstSize.x / pc.dstSize.y;
    float d = length(centred * float2(aspect, 1.0));
    float v = smoothstep(pc.shape.x, pc.shape.x + max(pc.shape.y, 1e-4), d) * pc.vignette.a;
    rgb = lerp(rgb, pc.vignette.rgb, v);

    if (pc.shape.z > 0.0)
    {
        float n = hash(floor(fragCoord.xy / max(pc.shape.w, 1.0)) + pc.misc.z) - 0.5;
        rgb += n * pc.shape.z;
    }

    return float4(saturate(rgb) * c.a, c.a);
}
