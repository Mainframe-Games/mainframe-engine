shader_type spatial;
render_mode cull_disabled;
// Neon's signs (World/NeonCity.cs): a made-up word from the signs atlas glowing on a dark panel. On a MultiMesh,
// INSTANCE_CUSTOM is (word index, hue, brightness, flicker seed) (CityLayout's CitySign). `vertical` picks the atlas'
// vertical words (the blades sticking out of the facades) or its horizontal ones (the boards over the shops); the panel's
// size in metres is `panel` times the instance's scale, and the word keeps its shape (stretched at most 1.6 times).
uniform sampler2D words : source_color, filter_linear_mipmap, repeat_disable;
uniform float vertical = 0.0;
uniform vec2 panel = vec2(1.0, 1.0);
uniform float energy = 6.0;

varying vec4 v_custom;
varying vec2 v_size;

float hash11(float p) {
	p = fract(p * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

vec3 neon(float h) {
	float k = floor(fract(h) * 6.0);
	vec3 c = vec3(1.0, 0.12, 0.62);
	c = k > 0.5 ? vec3(0.1, 0.85, 1.0) : c;
	c = k > 1.5 ? vec3(0.55, 0.22, 1.0) : c;
	c = k > 2.5 ? vec3(1.0, 0.55, 0.12) : c;
	c = k > 3.5 ? vec3(0.35, 1.0, 0.55) : c;
	c = k > 4.5 ? vec3(1.0, 0.18, 0.2) : c;
	return c;
}

void vertex() {
	v_custom = INSTANCE_CUSTOM;
	v_size = panel * vec2(length((MODEL_MATRIX * vec4(1.0, 0.0, 0.0, 0.0)).xyz), length((MODEL_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz));
}

void fragment() {
	float index = mod(floor(v_custom.x + 0.5), 8.0);
	float cellAspect = vertical > 0.5 ? 0.5 : 2.0;
	// The word's box on the panel (metres), centred, keeping its aspect within a 1.6x stretch.
	vec2 box = v_size * 0.9;
	float a = box.x / box.y;
	if (a > cellAspect * 1.6) {
		box.x = box.y * cellAspect * 1.6;
	} else if (a < cellAspect / 1.6) {
		box.y = box.x / (cellAspect / 1.6);
	}
	vec2 q = ((UV - 0.5) * v_size) / box + 0.5;
	float inside = step(0.0, q.x) * step(q.x, 1.0) * step(0.0, q.y) * step(q.y, 1.0);
	vec2 origin = vertical > 0.5 ? vec2(index * 0.125, 0.5) : vec2(mod(index, 4.0) * 0.25, floor(index / 4.0) * 0.25);
	vec2 size = vertical > 0.5 ? vec2(0.125, 0.5) : vec2(0.25, 0.25);
	vec2 uv = origin + clamp(q, 0.0, 1.0) * size;
	// Close up the strokes are crisp; far away the mips average them with the gaps, so the panel would turn into one
	// flat block of light (which the tonemapper bleaches): there it dims to the average.
	float raw = texture(words, uv).r;
	float far = clamp(length(fwidth(uv)) * 400.0 - 0.5, 0.0, 1.0);
	float letters = mix(smoothstep(0.05, 0.5, raw), raw * 0.6, far) * inside;
	float halo = textureLod(words, uv, 4.0).r * inside * (1.0 - far * 0.7);

	// Flicker: most signs hum steadily, some stutter, a few have a dead stroke that sputters.
	float f = v_custom.w;
	float tick = floor(TIME * 12.0 + f * 100.0);
	float stutter = step(0.8, f) * step(0.75, hash11(tick));
	float on = 1.0 - stutter * 0.85;
	vec3 colour = neon(v_custom.y);
	float b = v_custom.z <= 0.0 ? 1.0 : v_custom.z;
	ALBEDO = vec3(0.02, 0.02, 0.025);
	ROUGHNESS = 0.35;
	EMISSION = colour * (letters * energy + halo * energy * 0.4 + 0.08) * b * on;
}
