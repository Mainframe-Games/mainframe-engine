shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
// The pools of the cars' head and tail lights on the wet road (World/CityTraffic.cs): seen from the apartment, rivers of
// white and red light along the streets. UV.x runs across the pool, UV.y from the car (0) to its far end (1).
uniform vec4 color : source_color = vec4(1.0, 0.9, 0.75, 1.0);
uniform float energy = 1.5;

void fragment() {
	float along = 1.0 - UV.y;
	float across = 1.0 - abs(UV.x * 2.0 - 1.0);
	ALBEDO = vec3(0.0);
	EMISSION = color.rgb * energy * along * along * pow(across, 1.5);
}
