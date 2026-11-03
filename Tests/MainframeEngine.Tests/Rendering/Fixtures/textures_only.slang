import mainframe.spatial;
// Fixture (SurfaceShaderTests): a material of textures only (slangc drops its empty buffer).

struct Material
{
    Sampler2D a;
    [color] Sampler2D b;
};

void fragment(inout SurfaceFragment s, Material m)
{
    s.albedo = m.a.Sample(s.uv).rgb * m.b.Sample(s.uv).rgb;
}
