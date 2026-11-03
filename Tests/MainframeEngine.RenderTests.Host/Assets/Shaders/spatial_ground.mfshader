shader_type spatial;
// Render-test spatial shader (ADR 0205): procedural ground from the world position, as Crash Site Defense's streamed
// chunks do. A nearest-filtered biome map picks one of two palettes per 4 m cell (sampled at a wobbled world position),
// value noise mottles it, a hash grid adds specks; lit by the engine and shadowed. Uniforms: a colour array, scalars and
// a sampler with hints.
uniform vec4 palette[4] : source_color;
uniform float cell_size = 4.0;
uniform float mottle_scale = 0.6;
uniform float speck_density : hint_range(0.0, 1.0) = 0.08;
uniform sampler2D biome_map : filter_nearest, repeat_disable;

const float WOBBLE = 0.35;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void fragment() {
	vec2 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xz;
	vec2 wobble = vec2(value_noise(world * 0.7), value_noise(world * 0.7 + 17.0)) - 0.5;
	vec2 map_uv = (world + wobble * WOBBLE * cell_size) / (cell_size * 4.0) + 0.5;
	float biome = texture(biome_map, map_uv).r;
	vec3 low = mix(palette[0].rgb, palette[1].rgb, value_noise(world * mottle_scale));
	vec3 high = mix(palette[2].rgb, palette[3].rgb, value_noise(world * mottle_scale + 5.0));
	vec3 albedo = mix(low, high, step(0.5, biome));
	float speck = step(1.0 - speck_density, hash(floor(world * 8.0)));
	ALBEDO = albedo * (1.0 - 0.45 * speck);
	ROUGHNESS = 0.9;
}
