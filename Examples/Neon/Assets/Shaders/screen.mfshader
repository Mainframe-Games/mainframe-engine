shader_type spatial;
render_mode unshaded;
// Neon's ad screens (billboards on the towers, the airships' flanks; World/NeonCity.cs): a loop of four procedural adverts
// for made-up products, cut every few seconds with a flash. UV is 0..1 over the screen (v down). INSTANCE_CUSTOM is
// (seed, hue, brightness, first advert) on a MultiMesh; a single mesh uses `seed`, `hue` and `brightness` instead. The words
// come from the signs atlas (the same made-up words as the neon signs).
uniform sampler2D words : source_color, filter_linear_mipmap, repeat_disable;
uniform float seed = 0.0;
uniform float hue = 0.0;
uniform float brightness = 0.0;
uniform float energy = 2.2;
uniform float aspect = 1.8;

varying vec4 v_custom;

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

// A horizontal word from the atlas (0..7) in the box lo..hi of the screen; 0 outside.
float word(vec2 p, vec2 lo, vec2 hi, float index) {
	vec2 q = (p - lo) / (hi - lo);
	if (q.x < 0.0 || q.x > 1.0 || q.y < 0.0 || q.y > 1.0) {
		return 0.0;
	}
	float i = mod(floor(index), 8.0);
	vec2 cell = vec2(mod(i, 4.0) * 0.25, floor(i / 4.0) * 0.25);
	return texture(words, cell + q * 0.25).r;
}

void vertex() {
	v_custom = INSTANCE_CUSTOM;
}

void fragment() {
	float s = v_custom.x + seed;
	float h = v_custom.y + hue;
	float b = v_custom.z + brightness;
	b = b <= 0.0 ? 1.0 : b;
	vec2 p = UV;
	float t = TIME + s * 37.0;
	float slide = mod(floor(t / 5.0) + v_custom.w, 4.0);
	float local = fract(t / 5.0) * 5.0;
	vec3 a = neon(h + slide * 0.37);
	vec3 c2 = neon(h + 0.5 + slide * 0.21);
	vec3 col = vec3(0.0);
	vec2 q = vec2((p.x - 0.5) * aspect, p.y - 0.5);

	if (slide < 0.5) {
		// A logo: a pulsing ring and a word under it, a ticker along the bottom.
		float ring = 1.0 - smoothstep(0.0, 0.02, abs(length(q - vec2(0.0, -0.08)) - 0.22 - 0.02 * sin(t * 4.0)));
		col = mix(a * 0.08, c2 * 0.25, p.y) + a * ring;
		col += vec3(1.0) * word(p, vec2(0.5 - 0.3 / aspect * 1.8, 0.62), vec2(0.5 + 0.3 / aspect * 1.8, 0.86), s * 8.0);
		float ticker = step(0.9, p.y) * step(0.5, fract(p.x * 14.0 + t * 0.8));
		col += c2 * ticker * 0.6;
	} else if (slide < 1.5) {
		// A sale: diagonal bars sweeping across and a big word.
		float bars = step(0.5, fract((p.x * aspect + p.y) * 3.0 - t * 0.7));
		col = mix(a * 0.6, c2 * 0.15, bars);
		float w = word(p, vec2(0.1, 0.25), vec2(0.9, 0.75), s * 8.0 + 3.0);
		col = mix(col, vec3(1.0), w);
	} else if (slide < 2.5) {
		// An eye: iris rings, a pupil that looks about, a blink.
		vec2 look = vec2(sin(t * 0.7) * 0.08, cos(t * 0.5) * 0.04);
		float r = length(q - look);
		float lid = 0.28 * (1.0 - step(4.4, local) * (1.0 - abs(local - 4.7) / 0.3));
		float eye = step(abs(q.y), lid * sqrt(max(0.0, 1.0 - q.x * q.x / 0.45)));
		float iris = 1.0 - smoothstep(0.17, 0.18, r);
		float pupil = 1.0 - smoothstep(0.06, 0.07, r);
		float rings = 0.5 + 0.5 * sin(r * 120.0 - t * 2.0);
		col = mix(vec3(0.01), mix(vec3(0.9), a * rings, iris) * (1.0 - pupil), eye);
		col += c2 * 0.15 * (1.0 - eye);
	} else {
		// A product: a can turning on a gradient, a word beside it.
		vec2 cq = q - vec2(-0.45, 0.0);
		float can = step(abs(cq.x), 0.13) * step(abs(cq.y), 0.32);
		float shade = 0.5 + 0.5 * sin(cq.x / 0.13 * 1.57 + t * 2.0);
		col = mix(c2 * 0.1, a * 0.3, p.x);
		col = mix(col, mix(a, vec3(1.0), shade * 0.6) * (0.4 + 0.6 * shade), can);
		col += vec3(1.0) * word(p, vec2(0.45, 0.3), vec2(0.95, 0.7), s * 8.0 + 5.0);
	}

	// The cut between adverts: a white flash and a roll.
	float cut = 1.0 - smoothstep(0.0, 0.18, local);
	col = mix(col, vec3(1.0), cut * 0.7);
	// LED pixels close up, scanlines.
	vec2 px = p * vec2(160.0 * aspect, 160.0);
	vec2 aa = fwidth(px);
	float grid = mix(0.7 + 0.3 * step(0.25, fract(px.x)) * step(0.25, fract(px.y)), 0.85, clamp(max(aa.x, aa.y) - 0.5, 0.0, 1.0));
	float scan = 0.9 + 0.1 * sin(p.y * 600.0 - t * 30.0);
	ALBEDO = vec3(0.0);
	EMISSION = col * grid * scan * energy * b;
}
