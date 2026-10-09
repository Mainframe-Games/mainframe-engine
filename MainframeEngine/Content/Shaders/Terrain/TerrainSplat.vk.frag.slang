// TerrainSplatMaterial3D (ADR 0156): set 0 frame + lights + sky lighting, set 1 shadows, set 2 the terrain splat set
// (TerrainSplatGpu in C#, MeshRenderer's second pipeline layout). Vertex shader: Mesh/Mesh.vk.vert (UV = terrain UV,
// which is the splat maps' UV on Terrain3D chunks). Linear HDR output, PBR, fogged.
//
// Fragment-stage budget (≤ 16 sampled images and ≤ 16 samplers per stage, ≤ 4 sets): shadows 6 combined samplers
// (set 1), the sky's IBL 3 combined samplers (set 0), this set 2 samplers + 5 images = 14 images, 11 samplers, 3 sets.
//
// Per fragment: the four strongest of the eight splat weights (two in the far band) → each layer sampled planar (world
// XZ, hex-tiled in the near band) and/or triplanar on slopes → height-blended weights → albedo × tint, whiteout-blended
// normals, ORM → macro variation → shadeLightsPbr → applyFog. Every texture lookup uses SampleGrad with gradients taken
// at the top, so the dynamic branches (skipped layers, bands, slopes) keep mip selection defined.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "fog.slang"

static const int kMaxLayers = 8;

// TerrainSplatParams in C# (std140, 304 bytes).
struct TerrainSplatParams
{
    float4 layers[8]; // x = 1 / tiling (repeats per metre), y = height blend contrast, z = normal scale, w = unused
    float4 tints[8];  // rgb = linear tint
    float4 bands;     // x = detail distance, y = far distance, z = 1 / far tiling scale, w = macro strength
    float4 slope;     // x = cos(triplanar start), y = cos(triplanar end), z = 1 / macro scale (m), w = layer count
    float4 options;   // x = hex-tiling on (1/0)
};

[[vk::binding(0, 2)]] ConstantBuffer<TerrainSplatParams> splat;
[[vk::binding(1, 2)]] SamplerState layerSampler;   // linear, repeat, anisotropic, mipmapped
[[vk::binding(2, 2)]] SamplerState mapSampler;     // linear, clamp
[[vk::binding(3, 2)]] Texture2DArray albedoLayers; // sRGB rgb, a = height
[[vk::binding(4, 2)]] Texture2DArray normalLayers; // linear, OpenGL convention
[[vk::binding(5, 2)]] Texture2DArray ormLayers;    // linear: R = occlusion, G = roughness, B = metallic
[[vk::binding(6, 2)]] Texture2D weights0;          // layers 0–3
[[vk::binding(7, 2)]] Texture2D weights1;          // layers 4–7

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inColor; // white from Mesh.vk.vert; multiplies the albedo
};

// One layer's lookup: albedo (a = height), the normal map in its UV frame (x along +u, y along +v, z out) and ORM.
struct LayerTexel
{
    float4 albedo;
    float3 n;
    float3 orm;
};

// One layer's contribution in world space.
struct LayerSample
{
    float4 albedo;
    float3 N;
    float3 orm;
};

// ── Hashes and noise ──────────────────────────────────────────────────────────────────────────────────────────────

uint pcgHash(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float3 hash3(int2 p)
{
    uint a = pcgHash(uint(p.x) + pcgHash(uint(p.y)));
    uint b = pcgHash(a);
    uint c = pcgHash(b);
    return float3(float(a), float(b), float(c)) * (1.0 / 4294967296.0);
}

float valueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = p - i;
    float2 u = f * f * (3.0 - 2.0 * f);
    int2 c = int2(i);
    float a = hash3(c).x;
    float b = hash3(c + int2(1, 0)).x;
    float d = hash3(c + int2(0, 1)).x;
    float e = hash3(c + int2(1, 1)).x;
    return lerp(lerp(a, b, u.x), lerp(d, e, u.x), u.y);
}

// ── Texture lookups ───────────────────────────────────────────────────────────────────────────────────────────────

LayerTexel fetchLayer(int layer, float2 uv, float2 dx, float2 dy)
{
    float3 at = float3(uv, float(layer));
    LayerTexel t;
    t.albedo = albedoLayers.SampleGrad(layerSampler, at, dx, dy);
    float3 n = normalLayers.SampleGrad(layerSampler, at, dx, dy).xyz * 2.0 - 1.0;
    n.y = -n.y; // OpenGL green points up the image, towards −v (Vulkan UV): flip into the UV frame
    n.xy *= splat.layers[layer].z;
    t.n = n;
    t.orm = ormLayers.SampleGrad(layerSampler, at, dx, dy).rgb;
    return t;
}

float2 rotate2(float2 v, float s, float c)
{
    return float2(c * v.x - s * v.y, s * v.x + c * v.y);
}

// Hex-tiling (Mikkelsen 2022, "Practical Real-Time Hex-Tiling", JCGT 11(2); written from the paper): the UV plane is
// covered by a triangle grid; each of the three vertices around a point carries a random rotation and offset of the
// texture, and the three lookups are blended with barycentric weights sharpened by a power and by the samples'
// luminance, so seams vanish while the texture keeps its contrast. Normals are rotated back into the UV frame.
LayerTexel fetchLayerHex(int layer, float2 uv, float2 dx, float2 dy)
{
    const float kScale = 3.4641016; // 2√3: hex cells about a third of a texture repeat
    const float2 st = uv * kScale;
    // Skew into a grid of equilateral triangles.
    float2 skewed = float2(st.x - 0.57735027 * st.y, 1.15470054 * st.y);
    int2 base = int2(floor(skewed));
    float3 t = float3(frac(skewed), 0.0);
    t.z = 1.0 - t.x - t.y;
    float s = t.z < 0.0 ? 1.0 : 0.0;
    float s2 = 2.0 * s - 1.0;
    float3 w = float3(-t.z * s2, s - t.y * s2, s - t.x * s2);
    int2 v[3] = { base + int2(int(s), int(s)), base + int2(int(s), 1 - int(s)), base + int2(1 - int(s), int(s)) };

    LayerTexel texel[3];
    float3 luminance;
    for (int k = 0; k < 3; k++)
    {
        float3 h = hash3(v[k]);
        float angle = (h.z - 0.5) * 2.0 * PI;
        float sn = sin(angle);
        float cs = cos(angle);
        // The vertex's centre back in UV space (inverse skew, then unscale).
        float2 centre = float2(float(v[k].x) + 0.5 * float(v[k].y), float(v[k].y) * 0.8660254) / kScale;
        float2 local = rotate2(uv - centre, sn, cs) + centre + h.xy;
        texel[k] = fetchLayer(layer, local, rotate2(dx, sn, cs), rotate2(dy, sn, cs));
        texel[k].n.xy = rotate2(texel[k].n.xy, -sn, cs); // back into the unrotated UV frame
        luminance[k] = dot(texel[k].albedo.rgb, float3(0.2126, 0.7152, 0.0722));
    }

    // Sharpen the barycentric weights (power 7) and favour the brighter sample (contrast 0.6), as the paper does.
    float3 weights = lerp(float3(1.0), luminance, 0.6) * pow(max(w, float3(0.0)), float3(7.0));
    weights /= max(weights.x + weights.y + weights.z, 1e-6);

    LayerTexel r;
    r.albedo = texel[0].albedo * weights.x + texel[1].albedo * weights.y + texel[2].albedo * weights.z;
    r.n = texel[0].n * weights.x + texel[1].n * weights.y + texel[2].n * weights.z;
    r.orm = texel[0].orm * weights.x + texel[1].orm * weights.y + texel[2].orm * weights.z;
    return r;
}

// Whiteout blend of a UV-frame normal over the geometric normal, for a projection with UV axes ua, va and outward axis
// na (Golus, "Normal Mapping for a Triplanar Shader"): the map's slopes add to the surface's own.
float3 whiteout(float3 n, float3 Ngeo, float3 ua, float3 va, float3 na)
{
    float3 local = float3(dot(Ngeo, ua), dot(Ngeo, va), dot(Ngeo, na));
    float3 b = float3(n.xy + local.xy, abs(n.z) * local.z);
    return ua * b.x + va * b.y + na * b.z;
}

LayerSample toWorld(LayerTexel t, float3 Ngeo, float3 ua, float3 va, float3 na)
{
    LayerSample s;
    s.albedo = t.albedo;
    s.N = whiteout(t.n, Ngeo, ua, va, na);
    s.orm = t.orm;
    return s;
}

// One projection of a layer: UV = (P·ua, P·va) × tiling.
LayerSample sampleProjection(int layer, float3 P, float3 dPdx, float3 dPdy, float tiling, float3 Ngeo,
                             float3 ua, float3 va, float3 na, bool hex)
{
    float2 uv = float2(dot(P, ua), dot(P, va)) * tiling;
    float2 dx = float2(dot(dPdx, ua), dot(dPdx, va)) * tiling;
    float2 dy = float2(dot(dPdy, ua), dot(dPdy, va)) * tiling;
    LayerTexel t = hex ? fetchLayerHex(layer, uv, dx, dy) : fetchLayer(layer, uv, dx, dy);
    return toWorld(t, Ngeo, ua, va, na);
}

LayerSample mixSamples(LayerSample a, LayerSample b, float t)
{
    LayerSample r;
    r.albedo = lerp(a.albedo, b.albedo, t);
    r.N = lerp(a.N, b.N, t);
    r.orm = lerp(a.orm, b.orm, t);
    return r;
}

// A layer at a point: planar (world XZ, optionally hex-tiled) below the triplanar slope, triplanar (plain lookups)
// above it, both blended in between.
LayerSample sampleLayer(int layer, float3 P, float3 dPdx, float3 dPdy, float tiling, float3 Ngeo, float triplanar, bool hex)
{
    float3 ny = float3(0.0, Ngeo.y < 0.0 ? -1.0 : 1.0, 0.0);
    LayerSample planar;
    if (triplanar < 1.0)
        planar = sampleProjection(layer, P, dPdx, dPdy, tiling, Ngeo, float3(1, 0, 0), float3(0, 0, 1), ny, hex);
    if (triplanar <= 0.0)
        return planar;

    float3 blend = pow(abs(Ngeo), float3(4.0));
    blend /= max(blend.x + blend.y + blend.z, 1e-6);
    float3 nx = float3(Ngeo.x < 0.0 ? -1.0 : 1.0, 0.0, 0.0);
    float3 nz = float3(0.0, 0.0, Ngeo.z < 0.0 ? -1.0 : 1.0);
    // Side projections keep the image upright (v down the world's −Y).
    LayerSample x = sampleProjection(layer, P, dPdx, dPdy, tiling, Ngeo, float3(0, 0, 1), float3(0, -1, 0), nx, false);
    LayerSample y = sampleProjection(layer, P, dPdx, dPdy, tiling, Ngeo, float3(1, 0, 0), float3(0, 0, 1), ny, false);
    LayerSample z = sampleProjection(layer, P, dPdx, dPdy, tiling, Ngeo, float3(1, 0, 0), float3(0, -1, 0), nz, false);
    LayerSample tri;
    tri.albedo = x.albedo * blend.x + y.albedo * blend.y + z.albedo * blend.z;
    tri.N = x.N * blend.x + y.N * blend.y + z.N * blend.z;
    tri.orm = x.orm * blend.x + y.orm * blend.y + z.orm * blend.z;
    return triplanar >= 1.0 ? tri : mixSamples(planar, tri, triplanar);
}

// ── Layer selection and blending ──────────────────────────────────────────────────────────────────────────────────

// The `count` strongest weights (indices into w, descending; ties keep the lower layer) and their sum.
float selectStrongest(float w[kMaxLayers], int count, out int index[4], out float weight[4])
{
    uint taken = 0u;
    float sum = 0.0;
    for (int k = 0; k < 4; k++)
    {
        index[k] = 0;
        weight[k] = 0.0;
        if (k >= count)
            continue;
        int best = -1;
        float bestWeight = 0.0;
        for (int l = 0; l < kMaxLayers; l++)
        {
            if ((taken & (1u << uint(l))) == 0u && w[l] > bestWeight)
            {
                best = l;
                bestWeight = w[l];
            }
        }
        if (best < 0)
            continue;
        taken |= 1u << uint(best);
        index[k] = best;
        weight[k] = bestWeight;
        sum += bestWeight;
    }
    return sum;
}

// The blended surface of one distance band: up to `count` layers, height-blended.
LayerSample shadeBand(float w[kMaxLayers], int count, float3 P, float3 dPdx, float3 dPdy, float3 Ngeo, float triplanar,
                      float tilingScale, bool hex)
{
    int index[4];
    float weight[4];
    float sum = selectStrongest(w, count, index, weight);
    if (sum <= 1e-4)
    {
        index[0] = 0; // no paint at all: layer 0
        weight[0] = 1.0;
        sum = 1.0;
    }

    LayerSample samples[4];
    float height[4];
    float best = -1e9;
    for (int k = 0; k < 4; k++)
    {
        weight[k] /= sum;
        height[k] = 0.0;
        if (weight[k] <= 1e-3)
        {
            weight[k] = 0.0;
            continue;
        }
        int layer = index[k];
        samples[k] = sampleLayer(layer, P, dPdx, dPdy, splat.layers[layer].x * tilingScale, Ngeo, triplanar, hex);
        height[k] = samples[k].albedo.a + weight[k];
        best = max(best, height[k]);
    }

    // Height blending: a layer survives where its height + weight comes within its contrast of the highest.
    float total = 0.0;
    for (int k = 0; k < 4; k++)
    {
        if (weight[k] <= 0.0)
            continue;
        float contrast = max(splat.layers[index[k]].y, 1e-3);
        weight[k] = max(height[k] - (best - contrast), 0.0);
        total += weight[k];
    }

    LayerSample r;
    r.albedo = float4(0.0);
    r.N = float3(0.0);
    r.orm = float3(0.0);
    for (int k = 0; k < 4; k++)
    {
        if (weight[k] <= 0.0)
            continue;
        float b = weight[k] / max(total, 1e-6);
        r.albedo += float4(samples[k].albedo.rgb * splat.tints[index[k]].rgb, samples[k].albedo.a) * b;
        r.N += samples[k].N * b;
        r.orm += samples[k].orm * b;
    }
    return r;
}

[shader("fragment")]
float4 main(FsIn input) : SV_Target0
{
    float3 P = input.inWorldPos;
    float3 dPdx = ddx(P);
    float3 dPdy = ddy(P);
    float3 Ngeo = normalize(input.inNormal);

    // Splat weights: bilinear between cell centres (unmipmapped maps).
    float4 w0 = weights0.SampleLevel(mapSampler, input.inUV, 0.0);
    float4 w1 = weights1.SampleLevel(mapSampler, input.inUV, 0.0);
    float w[kMaxLayers] = { w0.r, w0.g, w0.b, w0.a, w1.r, w1.g, w1.b, w1.a };
    int layerCount = int(splat.slope.w);
    for (int l = 0; l < kMaxLayers; l++)
        if (l >= layerCount)
            w[l] = 0.0;

    // Slope: 0 below the triplanar start angle, 1 past the end angle (cosines of the angle from vertical).
    float triplanar = saturate((splat.slope.x - Ngeo.y) / max(splat.slope.x - splat.slope.y, 1e-4));

    float dist = length(lights.cameraPosition.xyz - P);
    float far = saturate((dist - splat.bands.x) / max(splat.bands.y - splat.bands.x, 1e-3));
    bool hex = splat.options.x > 0.5;

    LayerSample surface;
    if (far < 1.0)
        surface = shadeBand(w, 4, P, dPdx, dPdy, Ngeo, triplanar, 1.0, hex);
    if (far > 0.0)
    {
        LayerSample distant = shadeBand(w, 2, P, dPdx, dPdy, Ngeo, triplanar, splat.bands.z, false);
        surface = far >= 1.0 ? distant : mixSamples(surface, distant, far);
    }

    // Macro variation: two octaves of value noise vary brightness (and warmth, a little) over tens of metres.
    float macroStrength = splat.bands.w;
    float3 albedo = surface.albedo.rgb * input.inColor.rgb;
    if (macroStrength > 0.0)
    {
        float2 q = P.xz * splat.slope.z;
        float noise = valueNoise(q) * 0.65 + valueNoise(q * 3.7 + 17.3) * 0.35;
        float m = (noise - 0.5) * 2.0 * macroStrength;
        albedo *= max(float3(1.0) + m * float3(1.0, 0.9, 0.7), float3(0.0));
    }

    float3 N = surface.N;
    float len = length(N);
    N = len > 1e-5 ? N / len : Ngeo;

    PbrSurface s;
    s.albedo = albedo;
    s.ao = saturate(surface.orm.x);
    s.roughness = saturate(surface.orm.y);
    s.metallic = saturate(surface.orm.z);
    s.N = N;
    float3 color = shadeLightsPbr(s, Ngeo, P);
    return float4(applyFog(color, P), 1.0);
}
