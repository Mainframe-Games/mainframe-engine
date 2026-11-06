import mainframe.spatial;
// Fixture (ShaderMaterialSerializationTests): a cutout material whose parameters round-trip through a .mfres.

[alpha(Alpha.Cutout)]
struct Material
{
    float progress = 0.5;
    [color] float4 tint;
    float weights[3];
    float2 offsets[2];
    bool glow;
    Sampler2D noise;
};

void fragment(inout SurfaceFragment s, Material m)
{
    s.alpha = m.progress;
    s.alphaScissorThreshold = 0.5;
}
