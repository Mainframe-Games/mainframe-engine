import mainframe.spatial;
// Fixture (SurfaceShaderTests): a parameter outside struct Material, which the engine can't bind.

uniform float strength;

void fragment(inout SurfaceFragment s)
{
    s.albedo = float3(strength);
}
