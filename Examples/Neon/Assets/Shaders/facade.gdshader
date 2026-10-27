shader_type spatial;
// Neon's tower facades (World/NeonCity.cs): every kit tower is a MultiMesh instance whose INSTANCE_CUSTOM is
// (window seed, accent hue, lit fraction, style), all 0..1 (CityLayout.TowerPlacement.Custom). One shader draws the kit's
// four surfaces, told apart by `part`: 0 the facade (a window grid: UV.x is metres along the facade, the storeys come from
// the world height so a tower stretched up keeps square windows), 1 roofs and clutter, 2 the accent trims, 3 the beacons.
// Nothing here is lit by a light: the windows, bands and trims are emission and the post glow makes them neon.
uniform float part = 0.0;
uniform float ground_y = -70.0;
uniform float storey = 3.5;
uniform float window_energy = 2.4;
uniform float trim_energy = 6.0;

varying vec3 v_world;
varying vec4 v_custom;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// The city's neon palette: magenta, cyan, violet, amber, acid green, hot red.
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
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_custom = INSTANCE_CUSTOM;
}

void fragment() {
	float seed = v_custom.x * 173.0;
	float hue = v_custom.y;
	float style = v_custom.w;
	vec3 accent = neon(hue);

	if (part > 2.5) {
		// Aviation beacons: a slow red blink, out of step per tower.
		float blink = step(0.82, fract(TIME * 0.5 + v_custom.x * 7.0));
		ALBEDO = vec3(0.05, 0.0, 0.0);
		EMISSION = vec3(1.0, 0.05, 0.02) * (0.4 + 14.0 * blink);
	} else if (part > 1.5) {
		// Accent trims: the tower's neon colour, breathing slowly; a few towers chase a light round their bands.
		float breathe = 0.75 + 0.25 * sin(TIME * 0.8 + v_custom.x * 20.0);
		float chase = step(0.75, style) * step(0.85, fract(UV.x / 40.0 - TIME * 0.25));
		ALBEDO = vec3(0.02);
		ROUGHNESS = 0.4;
		EMISSION = accent * trim_energy * (breathe + chase * 2.0);
	} else if (part > 0.5) {
		// Roofs, ledges, clutter and masts: dark, wet.
		ALBEDO = vec3(0.022, 0.022, 0.026);
		ROUGHNESS = 0.45;
		METALLIC = 0.1;
	} else {
		float column = mix(1.5, 3.1, fract(style * 7.13));
		float curtain = step(0.72, style);        // a curtain wall: wide glass, thin mullions
		vec2 cell = vec2(UV.x / column, (v_world.y - ground_y) / storey);
		vec2 id = floor(cell);
		vec2 f = fract(cell);
		vec2 aa = fwidth(cell) + 1e-4;

		vec2 lo = mix(vec2(0.16, 0.24), vec2(0.04, 0.1), curtain);
		vec2 hi = mix(vec2(0.84, 0.84), vec2(0.96, 0.92), curtain);
		bool shop = id.y < 0.5;                   // the ground floor: shopfronts
		if (shop) {
			lo = vec2(0.03, 0.06);
			hi = vec2(0.97, 0.8);
		}
		float wx = smoothstep(lo.x - aa.x, lo.x + aa.x, f.x) * (1.0 - smoothstep(hi.x - aa.x, hi.x + aa.x, f.x));
		float wy = smoothstep(lo.y - aa.y, lo.y + aa.y, f.y) * (1.0 - smoothstep(hi.y - aa.y, hi.y + aa.y, f.y));
		float glass = wx * wy;

		// Rooms of two to four windows are lit together; the lit fraction is the tower's.
		float roomWidth = 2.0 + floor(hash12(vec2(id.y, seed)) * 3.0);
		float room = floor(id.x / roomWidth);
		float r = hash12(vec2(room, id.y) + seed);
		// Whole floors go dark (offices after hours), the rest are lit at the tower's fraction.
		float floorDark = step(0.72, hash12(vec2(id.y * 0.61, seed + 5.0)));
		float lit = step(r, v_custom.z * 0.75) * (1.0 - floorDark);
		float tint = hash12(vec2(room * 1.7, id.y * 3.1) + seed * 0.37);
		vec3 warm = vec3(1.0, 0.6, 0.3);
		vec3 cool = vec3(0.7, 0.85, 1.0);
		vec3 colour = tint < 0.55 ? warm : (tint < 0.93 ? cool : neon(tint * 5.0 + hue));
		float bright = mix(0.15, 1.2, pow(hash12(id + seed * 1.3), 1.5));
		// Blinds: a lit window is dimmer towards its top or bottom.
		bright *= mix(1.0, smoothstep(0.0, 0.7, f.y), step(0.6, hash12(id * 2.3 + seed)));
		// A few windows flicker, a few more show a television's blue.
		float fl = hash12(id.yx * 1.9 + seed);
		float tv = step(0.93, fl) * (0.6 + 0.4 * sin(TIME * 9.0 + fl * 50.0) * sin(TIME * 3.7 + id.x));
		colour = mix(colour, vec3(0.3, 0.5, 1.0), step(0.93, fl));
		bright *= mix(1.0, tv, step(0.93, fl));
		bright *= 1.0 - step(0.992, fl) * step(0.5, fract(sin(TIME * 23.0 + id.x * 7.0) * 43.0));
		if (shop) {
			lit = step(0.15, hash12(vec2(id.x * 0.37, seed)));
			colour = mix(warm, neon(hash12(vec2(room, seed)) + hue), 0.6);
			bright = 2.2;
		}
		vec3 emission = colour * bright * lit * glass * window_energy;

		// Accent bands: some towers have a neon line every few storeys.
		float every = floor(mix(5.0, 16.0, fract(style * 3.7)));
		float bandOn = step(0.45, fract(style * 11.0)) * step(mod(id.y, every), 0.5) * step(1.5, id.y);
		float band = bandOn * (1.0 - smoothstep(0.05, 0.05 + aa.y * 2.0, f.y));
		emission += accent * band * trim_energy * 0.5;

		// Far away the grid is smaller than a pixel: fade to its average so it does not shimmer.
		float blur = clamp(max(aa.x, aa.y) * 1.6 - 0.35, 0.0, 1.0);
		float area = (hi.x - lo.x) * (hi.y - lo.y);
		vec3 average = mix(warm, cool, 0.4) * v_custom.z * area * 0.75 * 0.72 * 0.55 * window_energy + accent * trim_energy * 0.5 * bandOn * 0.06;
		EMISSION = mix(emission, average, blur);

		float glassAmount = mix(glass, area, blur);
		ALBEDO = mix(vec3(0.035, 0.036, 0.045), vec3(0.008, 0.01, 0.016), glassAmount);
		ROUGHNESS = mix(0.75, 0.12, glassAmount);
		METALLIC = mix(0.05, 0.5, glassAmount);
	}
}
