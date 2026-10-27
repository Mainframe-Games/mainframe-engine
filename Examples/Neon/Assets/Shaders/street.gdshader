shader_type spatial;
// Neon's streets and pavements (World/NeonCity.cs): wet asphalt in the rain. The street meshes carry UV = (metres along the
// street, metres across from its centre line) and COLOR = (half width / 64, kind, 0, 1): kind 0 a street between two
// crossings, 1 a crossing (its UV runs along X, across Z, from its centre), 2 the pavement. Puddles come from a world-space
// noise: there the asphalt goes glossy (it mirrors the sky's glow) and picks up smeared colour from the signs above.
uniform float marking_energy = 1.4;

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

float line(float v, float centre, float half_width, float aa) {
	return 1.0 - smoothstep(half_width - aa, half_width + aa, abs(v - centre));
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float hw = COLOR.r * 64.0;
	float kind = COLOR.g;
	vec2 uv = UV;
	vec2 aa = fwidth(uv) + 1e-4;
	float far = clamp(max(aa.x, aa.y) * 4.0 - 0.5, 0.0, 1.0);

	// Wetness: broad puddles and streaky ruts.
	float n = value_noise(v_world.xz * 0.11) * 0.7 + value_noise(v_world.xz * 0.6) * 0.3;
	float puddle = smoothstep(0.48, 0.62, n);
	vec3 albedo = vec3(0.028, 0.028, 0.032);
	vec3 emission = vec3(0.0);
	float marks = 0.0;
	vec3 markColour = vec3(0.0);

	if (kind < 0.5) {
		float v = uv.y;
		// Centre: a double amber line on wide streets, a dashed white one on narrow ones.
		if (hw > 9.0) {
			marks += line(v, 0.22, 0.08, aa.y) + line(v, -0.22, 0.08, aa.y);
			markColour += vec3(1.0, 0.55, 0.1) * (line(v, 0.22, 0.08, aa.y) + line(v, -0.22, 0.08, aa.y));
		} else {
			float dash = step(fract(uv.x / 9.0), 0.5);
			marks += line(v, 0.0, 0.08, aa.y) * dash;
			markColour += vec3(0.9) * line(v, 0.0, 0.08, aa.y) * dash;
		}
		// Lanes: dashed white between them.
		float lanes = max(1.0, floor(hw / 3.4));
		for (int i = 1; i < 4; i++) {
			float c = float(i) * hw / lanes;
			float on = step(float(i), lanes - 0.5) * step(fract(uv.x / 9.0 + 0.25), 0.4);
			float l = (line(v, c, 0.07, aa.y) + line(v, -c, 0.07, aa.y)) * on;
			marks += l;
			markColour += vec3(0.85) * l;
		}
		// The kerbs: a cyan neon strip either side.
		float kerb = line(abs(v), hw - 0.35, 0.09, aa.y);
		marks += kerb;
		markColour += vec3(0.1, 0.8, 1.0) * kerb * 2.0;
		// Zebra crossings at both ends of the block.
		float len = COLOR.b * 1024.0;
		float end = min(uv.x, len - uv.x);
		float zebra = step(1.0, end) * step(end, 4.5) * step(0.5, fract(v / 1.2)) * step(abs(v), hw - 0.6);
		marks += zebra;
		markColour += vec3(0.75) * zebra * 0.5;
	} else if (kind < 1.5) {
		// A crossing: plain asphalt, a little more worn.
		albedo = vec3(0.034, 0.033, 0.036);
	} else {
		// Pavement: slabs.
		vec2 tile = fract(v_world.xz / 1.5);
		float joint = 1.0 - step(0.04, tile.x) * step(0.04, tile.y);
		albedo = mix(vec3(0.045, 0.044, 0.05), vec3(0.025), joint);
		puddle *= 0.7;
	}

	marks = clamp(marks * (1.0 - far), 0.0, 1.0);
	albedo = mix(albedo, vec3(0.4), marks * 0.5);
	emission += markColour * marking_energy * (1.0 - far);
	// Puddles: dark and glossy, holding smeared neon from above (a fake of the reflections the street cannot have).
	float smear = value_noise(v_world.xz * vec2(0.05, 0.05)) * value_noise(v_world.xz * 0.21 + 3.0);
	vec3 glowColour = mix(vec3(1.0, 0.15, 0.6), vec3(0.1, 0.75, 1.0), value_noise(v_world.xz * 0.008));
	emission += glowColour * puddle * smoothstep(0.15, 0.6, smear) * 0.08 * (1.0 - far * 0.5);
	ALBEDO = mix(albedo, albedo * 0.5, puddle);
	ROUGHNESS = mix(0.38, 0.04, puddle);
	METALLIC = 0.0;
	SPECULAR = 0.6;
	EMISSION = emission;
}
