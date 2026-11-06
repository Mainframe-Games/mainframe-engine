// TerrainMaterial3D, the Faceted terrain look (G8a.4): set 0 frame + lights + sky lighting, set 1 shadows, set 2 the
// standard material set (material.slang) with its slots reinterpreted. Vertex shader: Mesh/Mesh.vk.vert (UV = terrain UV:
// terrain-local XZ ÷ size, the cell maps' UV on Terrain3D chunks).
//
//   albedo slot   the surface map (one texel per cell, R = surface id; Terrain3D's data), fetched by texel
//   normal slot   the palette, 32 × 2 linear texels: row 0 each surface's colour, row 1 its stripe colour, alpha = bands on
//   emission slot the user map (one texel per cell, four channels), for the tint channel
//   params        material.albedo = the tint colour (linear rgb, a = tint strength),
//                 material.emission = x stripe width (m), y stripe angle (radians), z tint channel (−1 off), w band start (0..1)
//                 material.pbr.y = roughness; flags.y = shading (Blinn-Phong or PBR); params.xy = Blinn specular/shininess
//
// Per fragment: the exact face normal from the position's derivatives (flat facets on shared vertices), the cell's surface
// colour, optional world-aligned two-tone bands box-filtered so they fade to the average far away, the tint channel, the
// instance's decals, then the standard lighting and fog. Linear HDR output. Opaque.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "fog.slang"
#include "decals.slang"

static const int kPaletteSize = 32;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inColor; // white from Mesh.vk.vert
    [[vk::location(4)]] nointerpolation uint inDecalMask; // G6.5
};

// The integral of a square wave of period 2 that is 1 on [1, 2): floor(t / 2) + 2 · max(frac(t / 2) − ½, 0).
float stripeIntegral(float t)
{
    float h = t * 0.5;
    return floor(h) + 2.0 * max(frac(h) - 0.5, 0.0);
}

// The fraction of a pixel footprint [x − w/2, x + w/2] on stripes: exact box filtering, 0.5 once a pixel covers many.
float filteredStripe(float x, float w)
{
    w = max(w, 1e-4);
    return (stripeIntegral(x + 0.5 * w) - stripeIntegral(x - 0.5 * w)) / w;
}

int2 cellOf(Texture2D map, float2 uv)
{
    uint width, height;
    map.GetDimensions(width, height);
    int2 size = int2(int(width), int(height));
    return clamp(int2(floor(uv * float2(size))), int2(0), size - 1);
}

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
{
    float3 P = input.inWorldPos;
    float3 dPdx = ddx(P);
    float3 dPdy = ddy(P);
    // The face normal, flipped up (the winding of the derivatives depends on the view's handedness).
    float3 Nface = cross(dPdx, dPdy);
    float lenFace = length(Nface);
    float3 N = lenFace > 1e-12 ? Nface / lenFace : normalize(input.inNormal);
    if (N.y < 0.0)
        N = -N;

    // The surface id of the cell under the fragment, and its palette entry.
    int2 cell = cellOf(albedoTexture, input.inUV);
    uint id = uint(round(albedoTexture.Load(int3(cell, 0)).r * 255.0));
    int column = int(min(id, uint(kPaletteSize - 1)));
    float4 base = normalTexture.Load(int3(column, 0, 0));
    float4 stripe = normalTexture.Load(int3(column, 1, 0));
    float3 albedo = base.rgb;

    // The tint channel: a user channel blends toward the tint colour and gates the bands (cut grass turns banded).
    float channel = material.emission.z;
    float tint = 0.0;
    float bandsOn = 1.0;
    if (channel >= 0.0)
    {
        float4 user = emissionTexture.Load(int3(cellOf(emissionTexture, input.inUV), 0));
        int c = int(channel);
        float v = c == 0 ? user.r : c == 1 ? user.g : c == 2 ? user.b : user.a;
        tint = v * material.albedo.a;
        bandsOn = v >= material.emission.w ? 1.0 : 0.0;
    }

    if (stripe.a > 0.5 && bandsOn > 0.0)
    {
        float width = max(material.emission.x, 0.01);
        float angle = material.emission.y;
        float2 dir = float2(cos(angle), sin(angle));
        float x = dot(P.xz, dir) / width;
        float footprint = abs(dot(dPdx.xz, dir)) + abs(dot(dPdy.xz, dir)); // stripe units per pixel × width
        float s = filteredStripe(x, footprint / width);
        albedo = lerp(albedo, stripe.rgb, s);
    }

    albedo = lerp(albedo, material.albedo.rgb, saturate(tint)) * input.inColor.rgb;

    // G6.5: the decals over this chunk, before lighting.
    DecalSurface decal;
    decal.albedo = albedo;
    decal.N = N;
    decal.orm = float3(1.0, material.pbr.y, 0.0);
    decal.emission = float3(0.0);
    if (input.inDecalMask != 0u)
        applyDecals(input.inDecalMask, P, dPdx, dPdy, N, decal);

    float ssao = ambientOcclusionAt(fragCoord); // ADR 0165
    float3 color;
    if (material.flags.y == kShadingPbr)
    {
        PbrSurface s;
        s.albedo = decal.albedo;
        s.ao = decal.orm.x;
        s.roughness = decal.orm.y;
        s.metallic = decal.orm.z;
        s.N = decal.N;
        color = shadeLightsPbr(s, N, P, ssao);
    }
    else if (material.flags.y == kShadingUnshaded)
    {
        color = decal.albedo;
    }
    else
    {
        color = shadeLightsBlinnPhong(decal.albedo, decal.N, N, P, material.params.x, material.params.y, ssao);
    }

    return float4(applyFog(color + decal.emission, P), 1.0);
}
