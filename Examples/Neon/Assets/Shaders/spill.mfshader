shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
// Light on the wet ground (World/NeonCity.cs): the pools in front of the shops and under the street lamps. Nothing in the
// city is a light, so this is what a shop's window does to the pavement. COLOR is the pool's colour, UV.y runs from its
// bright edge or centre (0) to where it fades (1) and UV.x across it (the edges fade too).
uniform float energy = 1.0;

varying vec3 v_world;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x), mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float along = 1.0 - clamp(UV.y, 0.0, 1.0);
	float across = 1.0 - pow(abs(UV.x * 2.0 - 1.0), 4.0);
	// The wet ground breaks the pool up: brighter where water stands.
	float wet = 0.65 + 0.7 * value_noise(v_world.xz * 0.7) * value_noise(v_world.xz * 0.17 + 5.0);
	ALBEDO = vec3(0.0);
	EMISSION = COLOR.rgb * energy * along * along * across * wet;
}
