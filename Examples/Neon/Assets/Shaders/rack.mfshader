shader_type spatial;
render_mode unshaded;
// The server racks' lights (Tools/apartment/build_apartment.py, Decor_RackLeds): every light is a small quad of its own and
// UV.x is its kind + its seed. Kinds: 0 power, steady; 1 disk or link activity, green; 2 the same in amber; 3 a blue
// heartbeat; 4 a fault, a slow red. Activity comes in bursts, so a rack looks busy in patches rather than noisy all over.
uniform float energy = 5.0;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
	float kind = floor(UV.x + 0.001);
	float seed = fract(UV.x);
	vec3 colour = vec3(0.1, 1.0, 0.45);
	float on = 1.0;
	if (kind < 0.5) {
		// Power: steady, with a rare dip.
		on = 0.75 + 0.25 * (1.0 - step(0.985, hash12(vec2(floor(TIME * 0.7 + seed * 31.0), seed))));
	} else if (kind < 2.5) {
		float rate = mix(7.0, 16.0, hash12(vec2(seed, 3.0)));
		float tick = floor(TIME * rate + seed * 97.0);
		float burst = step(0.42, hash12(vec2(floor(TIME * mix(0.25, 0.7, seed) + seed * 13.0), seed + 1.0)));
		on = step(0.5, hash12(vec2(tick, seed))) * (0.08 + 0.92 * burst);
		if (kind > 1.5) {
			colour = vec3(1.0, 0.55, 0.08);
			on *= 0.9;
		}
	} else if (kind < 3.5) {
		// A heartbeat: two quick beats a second or so.
		float beat = fract(TIME * mix(0.7, 1.1, seed) + seed * 11.0);
		on = 0.04 + exp(-beat * 16.0) + 0.7 * exp(-max(beat - 0.18, 0.0) * 18.0) * step(0.18, beat);
		colour = vec3(0.15, 0.45, 1.0);
	} else {
		on = 0.25 + 0.75 * step(0.5, fract(TIME * 0.6 + seed * 7.0));
		colour = vec3(1.0, 0.06, 0.04);
	}

	ALBEDO = vec3(0.0);
	EMISSION = colour * on * energy;
}
