shader_type spatial;
// Procedural ground from the world position alone (ADR 0205), the way Crash Site Defense streams its chunks: low-frequency
// value noise picks a biome, a wobble breaks its border, mottling, darkened borders and specks finish it. Nothing is stored
// per pixel, so any number of meshes tile seamlessly.
uniform vec4 moss : source_color = vec4(0.36, 0.44, 0.24, 1.0);
uniform vec4 moss_light : source_color = vec4(0.48, 0.54, 0.29, 1.0);
uniform vec4 dust : source_color = vec4(0.58, 0.46, 0.33, 1.0);
uniform vec4 dust_light : source_color = vec4(0.70, 0.60, 0.45, 1.0);
uniform float biome_scale = 0.08;
uniform float speck_density : hint_range(0.0, 1.0) = 0.06;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

void fragment() {
	vec2 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
	float wobble = value_noise(world * 0.9) * 0.18;
	float biome = value_noise(world * biome_scale) + wobble;
	float border = 1.0 - smoothstep(0.0, 0.05, abs(biome - 0.6));
	vec3 low = mix(moss.rgb, moss_light.rgb, value_noise(world * 0.7));
	vec3 high = mix(dust.rgb, dust_light.rgb, value_noise(world * 0.7 + 9.0));
	vec3 albedo = mix(low, high, step(0.6, biome)) * (1.0 - 0.35 * border);
	float speck = step(1.0 - speck_density, hash(floor(world * 10.0)));
	ALBEDO = albedo * (1.0 - 0.4 * speck);
	ROUGHNESS = 0.92;
}
