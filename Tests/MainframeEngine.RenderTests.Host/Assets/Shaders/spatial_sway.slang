import mainframe.spatial;
// Render-test spatial shader (ADR 0205): wind sway of fungal stalks, displaced by world position and time in every pass
// (the shadow follows), more towards the tip (uv.y = 0 at the top), each MultiMesh instance out of step by its
// instanceCustom.x; the cap colour comes from instanceCustom.yzw through a varying.

[cull(Cull.Disabled), specular(Specular.BlinnPhong)]
struct Material
{
    [color] float3 stalk_color = float3(0.85, 0.80, 0.70);
    float sway = 0.45;
    float speed = 1.3;
};

struct Varyings
{
    float3 tint;
};

void vertex(inout SurfaceVertex v, Material m, out Varyings o)
{
    float3 world = mul(float4(v.vertex, 1.0), v.modelMatrix).xyz;
    float bend = (1.0 - v.uv.y) * (1.0 - v.uv.y);
    float phase = v.instanceCustom.x * TAU + world.x * 0.4 + world.z * 0.3;
    v.vertex.x += sin(v.time * m.speed + phase) * m.sway * bend;
    v.vertex.z += cos(v.time * m.speed * 0.7 + phase) * m.sway * 0.5 * bend;
    o.tint = v.instanceCustom.yzw;
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float cap = smoothstep(0.25, 0.15, s.uv.y);
    s.albedo = lerp(m.stalk_color, i.tint, cap);
    s.roughness = 0.6;
}
