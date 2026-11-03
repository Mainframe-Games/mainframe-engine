import mainframe.spatial;
// The pools of the cars' head and tail lights on the wet road (World/CityTraffic.cs): seen from the apartment, rivers of
// white and red light along the streets. uv.x runs across the pool, uv.y from the car (0) to its far end (1).

[unshaded, blend(Blend.Add), cull(Cull.Disabled), depthDraw(DepthDraw.Never)]
struct Material
{
    [color] float4 color = float4(1.0, 0.9, 0.75, 1.0);
    float energy = 1.5;
};

void fragment(inout SurfaceFragment s, Material m)
{
    float along = 1.0 - s.uv.y;
    float across = 1.0 - abs(s.uv.x * 2.0 - 1.0);
    s.albedo = float3(0.0);
    s.emission = m.color.rgb * m.energy * along * along * pow(across, 1.5);
}
