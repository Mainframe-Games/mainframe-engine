import mainframe.spatial;
// Render-test spatial shader (ADR 0205): an additive rim glow drawn as a next pass / overlay over another material
// (Blend.Add, unshaded): the view-space normal and view built-ins.

[blend(Blend.Add), unshaded, depthDraw(DepthDraw.Never), noFog]
struct Material
{
    [color] float4 rim_color = float4(0.2, 0.9, 1.0, 1.0);
    float power = 2.5;
};

void fragment(inout SurfaceFragment s, Material m)
{
    float rim = pow(1.0 - clamp(dot(s.normal, s.view), 0.0, 1.0), m.power);
    s.albedo = m.rim_color.rgb;
    s.alpha = rim * m.rim_color.a;
}
