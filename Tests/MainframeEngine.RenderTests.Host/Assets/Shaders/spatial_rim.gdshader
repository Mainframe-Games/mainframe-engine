shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, fog_disabled;
// Render-test spatial shader (ADR 0205): an additive rim glow drawn as a next pass / overlay over another material
// (blend_add, unshaded): the view-space NORMAL and VIEW built-ins.
uniform vec4 rim_color : source_color = vec4(0.2, 0.9, 1.0, 1.0);
uniform float power = 2.5;

void fragment() {
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), power);
	ALBEDO = rim_color.rgb;
	ALPHA = rim * rim_color.a;
}
