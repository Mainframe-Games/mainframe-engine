import mainframe.spatial;
// An example surface shader (docs/design/shaders.md#surface-shaders): put it on a mesh with a ShaderMaterial
// (Shader = Assets/Shaders/example.slang). The engine generates every pass around these two functions; vertex() runs in
// the shadow and depth passes too, so the surface and its shadow move together.
//
// After editing, rebuild its SPIR-V (needs slangc, from the Vulkan SDK or Slang's releases):
//     dotnet msbuild <Game>.Desktop -t:MainframeShaders

struct Material
{
    [color] float4 tint = float4(0.25, 0.6, 1.0, 1.0);
    [range(0.0, 0.5)] float wobble = 0.05;
    float glow = 0.6;
};

void vertex(inout SurfaceVertex v, Material m)
{
    v.vertex += v.normal * sin(v.time * 2.0 + v.vertex.y * 4.0) * m.wobble;
}

void fragment(inout SurfaceFragment s, Material m)
{
    float pulse = 0.5 + 0.5 * sin(s.time * 3.0);
    s.albedo = m.tint.rgb;
    s.roughness = 0.4;
    s.emission = m.tint.rgb * m.glow * pulse;
}
