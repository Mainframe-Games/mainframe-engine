import mainframe.spatial;
// Fungal stalks swaying in the wind (ADR 0205): vertex() moves each vertex by its world position and time, more towards
// the tip (uv.y = 0 at the top); every MultiMesh instance is out of step by instanceCustom.x and takes its cap colour from
// instanceCustom.yzw. The shadow pass runs the same vertex(), so the shadows sway too.

[cull(Cull.Disabled), specular(Specular.BlinnPhong)]
struct Material
{
    [color] float3 stalk_color = float3(0.86, 0.80, 0.70);
    float sway = 0.4;
    float speed = 1.4;
};

struct Varyings
{
    float3 cap;
};

void vertex(inout SurfaceVertex v, Material m, out Varyings o)
{
    float3 world = mul(float4(v.vertex, 1.0), v.modelMatrix).xyz;
    float bend = (1.0 - v.uv.y) * (1.0 - v.uv.y);
    float phase = v.instanceCustom.x * TAU + world.x * 0.4 + world.z * 0.3;
    v.vertex.x += sin(v.time * m.speed + phase) * m.sway * bend;
    v.vertex.z += cos(v.time * m.speed * 0.7 + phase) * m.sway * 0.5 * bend;
    o.cap = v.instanceCustom.yzw;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float cap = smoothstep(0.22, 0.12, s.uv.y);
    s.albedo = lerp(m.stalk_color, i.cap, cap);
    s.emission = i.cap * cap * 0.35;
    s.roughness = 0.55;
}
