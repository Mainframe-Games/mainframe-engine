import mainframe.spatial;
// Fixture (SurfaceShaderTests): every parameter type and attribute, every spatial render mode, varyings of each kind.

[cull(Cull.Disabled), blend(Blend.Add), unshaded, noFog, depthDraw(DepthDraw.Always), noDepthTest, worldVertexCoords, specular(Specular.BlinnPhong)]
struct Material
{
    [color] float4 palette[2];
    [range(0.0, 4.0)] float scale = 2.0;
    bool flag = true;
    int count = -3;
    uint mask = 7u;
    float2 offset = float2(0.5, -1.0);
    int3 cells = int3(1);
    float3x3 basis;
    [group("Transform")] float4x4 transform;
    [filter(Filter.Nearest), repeat(Repeat.Disabled)] Sampler2D biome;
    [defaultTexture(DefaultTexture.Normal)] Sampler2D detail;
    [color, defaultTexture(DefaultTexture.Black), repeat(Repeat.Enabled), filter(Filter.Linear)] Sampler2D dirt;
};

struct Varyings
{
    float3 world;
    nointerpolation int id;
    float3x3 basis;
    uint k;
};

void vertex(inout SurfaceVertex v, Material m, out Varyings o)
{
    o.world = v.vertex * m.scale;
    o.id = v.instanceId;
    o.basis = (float3x3)v.modelMatrix;
    o.k = m.mask;
}

void fragment(inout SurfaceFragment s, Varyings i, Material m)
{
    s.albedo = i.world * float(i.id) + i.basis[0] + m.biome.Sample(s.uv).rgb;
    s.alpha = 0.5;
}
