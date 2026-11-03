shader_type spatial;
// Neon's streets and pavements (World/NeonCity.cs): wet asphalt in the rain. The street meshes carry UV = (metres along the
// street, metres across from its centre line) and COLOR = (half width / 64, kind, length / 1024, 1): kind 0 a street
// between two crossings, 1 a crossing (its UV runs along X, across Z, from its centre), 2 the pavement.
//
// Nothing here can really mirror the city, so city_mirror() traces the reflected ray to an imaginary wall of shops and
// windows the far side of the street: a row of shopfronts low down, lit windows above, the sky's glow over them. The
// roughness of the wet surface smears it along the view, as it does on a real road at night.
uniform float marking_energy = 0.55;
// The menu: how much of the city the wet streets mirror.
uniform float reflections = 1.0;

varying vec3 v_world;

float hash11(float p) {
	p = fract(p * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

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

// The reflected ray from `p` meets a wall `dist` metres off: what is on it there? Only soft shapes: a road never mirrors
// a window sharply, it smears each light into a streak along the view.
vec3 city_mirror(vec3 p, vec3 r, float dist) {
	float flat_ = max(length(r.xz), 1e-3);
	float y = dist / flat_ * r.y;
	vec2 dir = r.xz / flat_;
	float along = dot(p.xz, vec2(-dir.y, dir.x));
	float a = along * 0.11;
	float blend = fract(a);
	blend = blend * blend * (3.0 - 2.0 * blend);
	float id = floor(a);
	vec3 shop0 = mix(vec3(1.0, 0.72, 0.45), neon(hash11(id * 0.37)), 0.55) * step(0.25, hash11(id * 0.71));
	vec3 shop1 = mix(vec3(1.0, 0.72, 0.45), neon(hash11((id + 1.0) * 0.37)), 0.55) * step(0.25, hash11((id + 1.0) * 0.71));
	vec3 c = mix(shop0, shop1, blend) * exp(-y / 7.0) * 1.3;
	float lit = value_noise(vec2(along * 0.28, y * 0.02));
	c += vec3(1.0, 0.72, 0.5) * smoothstep(0.35, 0.8, lit) * smoothstep(5.0, 12.0, y) * exp(-y / 80.0) * 0.55;
	c += vec3(0.3, 0.075, 0.27) * smoothstep(40.0, 200.0, y);
	return c;
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float hw = COLOR.r * 64.0;
	float kind = COLOR.g;
	vec2 uv = UV;
	vec2 aa = fwidth(uv) + 1e-4;
	vec2 p = v_world.xz;
	// How far off this is, for what must fade before it aliases: 1 m features at a few pixels (the street is always far).
	vec2 metre = fwidth(p) + 1e-4;
	float far = clamp(max(max(aa.x, aa.y), max(metre.x, metre.y)) * 12.0 - 0.6, 0.0, 1.0);

	// Wetness: damp everywhere, standing water in the dips and along the ruts.
	float broad = value_noise(p * 0.07);
	float fine = value_noise(p * 0.31);
	float puddle = smoothstep(0.38, 0.8, broad * 0.75 + fine * 0.25);
	float grit = mix(value_noise(p * 1.4), 0.5, far);

	vec3 albedo = mix(vec3(0.02, 0.02, 0.024), vec3(0.027, 0.027, 0.031), grit);
	float wall = 9.0;
	float marks = 0.0;
	vec3 markColour = vec3(0.0);

	if (kind < 0.5) {
		float v = uv.y;
		wall = hw + 8.0;
		// Patched asphalt: darker rectangles, and tyres polish the wheel lines.
		vec2 patchCell = floor(vec2(uv.x / 11.0, (v + 64.0) / 3.2));
		float patch = step(0.72, hash12(patchCell)) * 0.6;
		albedo *= 1.0 - 0.45 * patch;
		float lanes = max(1.0, floor(hw / 3.4));
		float lanePos = abs(fract(abs(v) / (hw / lanes)) - 0.5);
		float ruts = 1.0 - smoothstep(0.0, 0.12, abs(lanePos - 0.28));
		puddle = max(puddle, ruts * 0.55 * smoothstep(0.35, 0.7, broad));
		// The paint, worn away by the traffic.
		float worn = smoothstep(0.25, 0.6, value_noise(p * vec2(1.3, 1.3)));
		if (hw > 9.0) {
			float centre = line(v, 0.22, 0.08, aa.y) + line(v, -0.22, 0.08, aa.y);
			marks += centre;
			markColour += vec3(1.0, 0.62, 0.14) * centre;
		} else {
			float dash = step(fract(uv.x / 9.0), 0.5);
			marks += line(v, 0.0, 0.08, aa.y) * dash;
			markColour += vec3(0.9) * line(v, 0.0, 0.08, aa.y) * dash;
		}
		for (int i = 1; i < 4; i++) {
			float c = float(i) * hw / lanes;
			float on = step(float(i), lanes - 0.5) * step(fract(uv.x / 9.0 + 0.25), 0.4);
			float l = (line(v, c, 0.07, aa.y) + line(v, -c, 0.07, aa.y)) * on;
			marks += l;
			markColour += vec3(0.85) * l;
		}
		// Zebra crossings at both ends of the block, and the stop line.
		float len = COLOR.b * 1024.0;
		float end = min(uv.x, len - uv.x);
		float zebra = step(1.0, end) * step(end, 4.5) * step(0.5, fract(v / 1.2)) * step(abs(v), hw - 0.6);
		marks += zebra;
		markColour += vec3(0.8) * zebra;
		marks *= worn;
		markColour *= worn;
		// The gutter: the kerb's shadow and the water that runs along it.
		float gutter = smoothstep(hw - 1.1, hw - 0.1, abs(v));
		albedo *= 1.0 - 0.4 * gutter;
		puddle = max(puddle, gutter * 0.85);
		float kerb = smoothstep(hw - 0.22, hw - 0.12, abs(v));
		albedo = mix(albedo, vec3(0.16, 0.155, 0.15), kerb);
		puddle *= 1.0 - kerb;
	} else if (kind < 1.5) {
		// A crossing: plain asphalt, a little more worn.
		wall = 18.0;
		albedo *= 1.15;
	} else {
		// Pavement: slabs, some cracked or darker, wet joints.
		vec2 tile = p / vec2(1.6, 0.8);
		tile.x += step(0.5, fract(tile.y * 0.5)) * 0.5;
		vec2 tileId = floor(tile);
		vec2 tf = fract(tile);
		// The slabs go before they alias: from a few pixels each the pavement is one tone.
		float slabs = clamp(1.4 - max(fwidth(tile).x, fwidth(tile).y) * 7.0, 0.0, 1.0);
		float joint = (1.0 - step(0.035, tf.x) * step(0.07, tf.y)) * slabs;
		float shade = mix(1.0, 0.75 + 0.5 * hash12(tileId), slabs);
		albedo = mix(vec3(0.15, 0.14, 0.15) * shade, vec3(0.05), joint);
		puddle = max(puddle * 0.35, joint * 0.4);
		wall = 7.0;
	}

	marks = clamp(marks * (1.0 - far), 0.0, 1.0);
	albedo = mix(albedo, vec3(0.5), marks * 0.55);
	puddle = clamp(puddle, 0.0, 1.0);

	vec3 V = normalize(CAMERA_POSITION_WORLD - v_world);
	// The surface's ripples and grain tilt the mirror, more on the damp road than in standing water.
	float rough = mix(0.05, 0.012, puddle);
	vec2 ripple = mix(p * 1.7, p * 0.23, far);
	vec2 tilt = vec2(value_noise(ripple + TIME * 0.15) - 0.5, value_noise(ripple + 41.0 - TIME * 0.12) - 0.5) * rough * (1.0 - far * 0.7);
	vec3 n = normalize(vec3(tilt.x, 1.0, tilt.y));
	vec3 R = reflect(-V, n);
	R.y = abs(R.y);
	float fresnel = 0.03 + 0.97 * pow(1.0 - max(dot(V, n), 0.0), 5.0);
	vec3 mirror = city_mirror(v_world, normalize(R), wall);
	float wet = mix(0.55, 1.0, puddle);
	vec3 emission = mirror * (fresnel * 1.5 + 0.012) * wet * reflections;

	// The light that reaches the ground anyway: the sky's glow, and the street's colours from above.
	vec3 ambient = vec3(0.16, 0.08, 0.17);
	emission += albedo * ambient * 2.4;
	emission += markColour * marking_energy * (1.0 - far) * mix(0.8, 0.35, puddle);

	ALBEDO = albedo * (1.0 - 0.35 * puddle);
	ROUGHNESS = mix(0.4, 0.12, puddle);
	METALLIC = 0.0;
	SPECULAR = 0.6;
	EMISSION = emission;
}
