shader_type spatial;
// Neon's towers (World/NeonCity.cs): every kit tower is a MultiMesh instance whose INSTANCE_CUSTOM is (window seed, accent
// hue, lit fraction, style), all 0..1 (CityLayout.TowerPlacement.Custom). One shader draws the kit's five surfaces, told
// apart by `part`: 0 the facade, 1 roofs, structure and clutter, 2 the accent trims, 3 the beacons, 4 work lamps.
//
// The facade is a procedural wall, not a texture. UV.x is kit metres along the face; the storeys come from the kit height
// so that a tower stretched up keeps a whole number of storeys between the kit's ledges (`ledge_every`). A window has a
// recessed pane (the reveal shows at an angle), mullions, and behind the glass a room in parallax: interior mapping of a
// box several windows wide, with a lamp, a back wall, furniture and blinds. No light reaches the city (ADR 0218: its light
// is emission and glow), so city_light() fakes what would: the street's neon rising up the walls and the sky above.
uniform float part = 0.0;
uniform float ground_y = -70.0;
uniform float storey = 3.5;
uniform float ledge_every = 17.5;
uniform float window_energy = 1.5;
uniform float trim_energy = 3.6;
// The menu: 0 flat windows and plain walls, 1 rooms, reveals and wall detail.
uniform float detail = 1.0;
// The menu: scales the lit share of the windows.
uniform float lit_scale = 1.0;
// The menu: scales the trims and bands.
uniform float neon_scale = 1.0;

varying vec3 v_world;
varying vec3 v_normal;
varying vec4 v_custom;
varying vec2 v_scale;

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

// The light a surface would get: the street's colours rising up the walls, fading by the 20th floor, under a dim sky.
vec3 city_light(vec3 n, float height, vec3 p) {
	float street = exp(-max(height, 0.0) / 42.0);
	float a = value_noise(p.xz * 0.011 + 7.0);
	float b = value_noise(p.xz * 0.027 + 19.0);
	vec3 low = mix(vec3(1.0, 0.13, 0.46), vec3(0.14, 0.5, 1.0), smoothstep(0.32, 0.68, a));
	low = mix(low, vec3(1.0, 0.46, 0.16), smoothstep(0.55, 0.8, b));
	vec3 sky = vec3(0.16, 0.085, 0.27) * (0.5 + 0.5 * n.y);
	// The glow over downtown, ahead of the window wall, rims the faces that look at it.
	float rim = max(dot(n, vec3(-0.12, 0.0, -0.99)), 0.0);
	return sky * 0.5 + low * street * (0.42 + 0.6 * max(-n.y, 0.0)) + vec3(0.42, 0.12, 0.42) * rim * 0.09;
}

// What wet glass mirrors: the glow at the horizon, the dark sky, and rows of the city's lights.
vec3 glass_reflection(vec3 r) {
	float up = r.y;
	vec3 c = mix(vec3(0.3, 0.075, 0.27), vec3(0.016, 0.02, 0.05), smoothstep(0.0, 0.42, up));
	c = up < 0.0 ? mix(vec3(0.3, 0.075, 0.27), vec3(0.09, 0.03, 0.1), smoothstep(0.0, -0.35, up)) : c;
	vec2 q = vec2(atan(r.x, r.z) * 11.0, up * 19.0);
	vec2 g = floor(q);
	vec2 f = fract(q) - 0.5;
	float on = step(0.74, hash12(g)) * (1.0 - smoothstep(0.0, 0.9, abs(up) * 2.4));
	float dot_ = 1.0 - smoothstep(0.1, 0.42, length(f * vec2(1.0, 1.6)));
	return c + mix(vec3(1.0, 0.62, 0.34), neon(hash12(g + 3.0)), step(0.6, hash12(g + 9.0))) * on * dot_ * 0.75;
}

// One room behind the glass: `p` the point on the pane, `d` the ray (both metres, x along the wall, y up, z out of the
// wall), the box x0..x1, y0..y1 and `depth` deep, `rs` the room's random number. Returns its colour, lit to 1.
vec3 room(vec3 p, vec3 d, float x0, float x1, float y0, float y1, float depth, float rs) {
	float tx = ((d.x > 0.0 ? x1 : x0) - p.x) / d.x;
	float ty = ((d.y > 0.0 ? y1 : y0) - p.y) / d.y;
	float tz = (-depth - p.z) / d.z;
	float hit = min(tx, min(ty, tz));
	vec3 h = p + d * hit;
	vec2 size = vec2(x1 - x0, y1 - y0);
	vec2 b = (h.xy - vec2(x0, y0)) / size;
	float z = -h.z / depth; // 0 at the glass, 1 at the back wall

	vec3 paint = mix(vec3(0.85, 0.74, 0.6), vec3(0.62, 0.68, 0.74), step(0.6, rs));
	paint = mix(paint, vec3(0.6, 0.42, 0.34), step(0.82, rs));
	vec3 lamp = vec3(h.x - (x0 + x1) * 0.5, y1 - h.y, (h.z + depth * 0.5));
	float pool = 1.0 / (1.0 + dot(lamp, lamp) * 0.16);
	vec3 c;
	if (hit == tz) {
		c = paint * (0.5 + 0.5 * pool);
		// A door or a picture on the back wall, a shelf of books on some.
		float doorX = 0.2 + 0.6 * fract(rs * 7.3);
		float door = step(abs(b.x - doorX), 0.09) * step(b.y, 0.72);
		c = mix(c, c * 0.42, door);
		float art = step(abs(b.x - fract(doorX + 0.45)), 0.13) * step(abs(b.y - 0.58), 0.13) * step(0.4, fract(rs * 3.1));
		c = mix(c, neon(rs * 9.0) * 0.7, art);
		float shelf = step(0.72, fract(rs * 5.7)) * step(0.25, b.y) * step(b.y, 0.85) * step(fract(b.y * 5.0), 0.8);
		c = mix(c, vec3(0.2, 0.13, 0.1) * (0.6 + 0.8 * hash12(floor(b * vec2(38.0, 5.0)))), shelf * step(abs(b.x - doorX), 0.3) * (1.0 - door));
	} else if (hit == ty) {
		if (d.y > 0.0) {
			// The ceiling: bright round the lamp, a strip light in offices.
			float strip = step(0.5, fract(rs * 11.0)) * step(0.5, fract(h.z * 0.42)) * step(abs(fract(b.x * 3.0) - 0.5), 0.09);
			c = vec3(0.86, 0.84, 0.78) * (0.42 + 1.1 * pool) + vec3(1.4) * strip;
		} else {
			// The floor: carpet or boards.
			float boards = 0.82 + 0.18 * step(0.5, fract(h.x * 2.4 + rs * 9.0));
			c = mix(vec3(0.3, 0.2, 0.14), vec3(0.16, 0.17, 0.2), step(0.5, rs)) * boards * (0.45 + 0.8 * pool);
		}
	} else {
		c = paint * (0.34 + 0.55 * pool) * (1.0 - 0.25 * z);
	}

	// Furniture on a card across the room: a sofa or desk, a lamp, sometimes someone standing.
	float tc = (-depth * (0.38 + 0.26 * fract(rs * 13.0)) - p.z) / d.z;
	if (tc < hit) {
		vec3 q = p + d * tc;
		vec2 e = (q.xy - vec2(x0, y0)) / size;
		float seatX = fract(rs * 17.0);
		float seat = step(abs(e.x - seatX), 0.24) * step(e.y, 0.3 + 0.12 * step(abs(e.x - seatX + 0.2), 0.05));
		float person = step(0.72, fract(rs * 23.0)) * (step(length((e - vec2(fract(rs * 29.0), 0.68)) * vec2(size.x / size.y, 1.0)), 0.075) + step(abs(e.x - fract(rs * 29.0)), 0.045 * size.y / size.x) * step(e.y, 0.6));
		c = mix(c, vec3(0.05, 0.045, 0.05), clamp(seat + person, 0.0, 1.0));
	}

	return c * (0.55 + 0.45 * z);
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	v_custom = INSTANCE_CUSTOM;
	v_scale = vec2(length(MODEL_MATRIX[0].xyz), length(MODEL_MATRIX[1].xyz));
}

void fragment() {
	float seed = v_custom.x * 173.0;
	float hue = v_custom.y;
	float style = v_custom.w;
	vec3 accent = neon(hue);
	vec3 n = normalize(v_normal);
	float height = v_world.y - ground_y;

	if (part > 3.5) {
		// Work lamps on roofs and ledges: small, steady, cold or warm per tower.
		ALBEDO = vec3(0.04);
		EMISSION = mix(vec3(1.0, 0.82, 0.6), vec3(0.75, 0.88, 1.0), step(0.5, fract(seed))) * 3.2;
	} else if (part > 2.5) {
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
		EMISSION = accent * trim_energy * neon_scale * (breathe + chase * 2.0);
	} else if (part > 0.5) {
		// Roofs, ledges, structure and clutter: dark wet concrete and steel in the city's light. Up-facing surfaces are
		// roofing (panels and gravel, gloss where the water stands); the rest gets seams and rust from the rain.
		vec2 aa2 = fwidth(v_world.xz) + 1e-4;
		float top = smoothstep(0.7, 0.95, n.y);
		float grit = value_noise(v_world.xz * 0.9) * 0.6 + value_noise(v_world.xz * 3.1) * 0.4;
		vec2 tile = abs(fract(v_world.xz / 2.4) - 0.5);
		float seam = (1.0 - smoothstep(0.46, 0.49, max(tile.x, tile.y))) * clamp(1.0 - max(aa2.x, aa2.y) * 2.0, 0.0, 1.0);
		float streak = value_noise(vec2(dot(v_world.xz, vec2(0.7, 0.7)) * 1.4, v_world.y * 0.08));
		vec3 side = mix(vec3(0.075, 0.075, 0.085), vec3(0.11, 0.08, 0.065), smoothstep(0.55, 0.8, streak));
		vec3 flat_ = mix(vec3(0.055, 0.055, 0.062), vec3(0.095, 0.09, 0.09), grit) * (0.8 + 0.2 * seam);
		vec3 albedo = mix(side, flat_, top);
		float puddle = smoothstep(0.55, 0.72, value_noise(v_world.xz * 0.21)) * top;
		ALBEDO = albedo * (1.0 - 0.5 * puddle);
		ROUGHNESS = mix(0.5, 0.08, puddle);
		METALLIC = 0.1;
		EMISSION = albedo * city_light(n, height, v_world) * 1.6;
	} else {
		float height_scale = max(v_scale.y, 0.05);
		// Storeys: a whole number between the kit's ledges, so a ledge always lands on a floor line.
		float span = ledge_every * height_scale;
		float floors_per = max(2.0, floor(span / storey + 0.5));
		float floor_h = span / floors_per;
		float kind = fract(style * 5.0 + v_custom.x);       // the wall: curtain, ribbon, fins or punched
		float bay = 6.0;                                    // kit metres between the piers (the kit's BAY)
		float column = bay / (2.0 + floor(fract(style * 7.13) * 3.0));
		vec2 cell = vec2(UV.x / column, height / floor_h);
		vec2 id = floor(cell);
		vec2 f = fract(cell);
		vec2 aa = fwidth(cell) + 1e-4;
		float column_m = column * v_scale.x;

		// Curtain walls are mostly glass, ribbon walls long and low, punched walls small windows in a thick wall.
		float curtain = step(kind, 0.3);
		float ribbon = step(0.3, kind) * step(kind, 0.55);
		float fins = step(0.55, kind) * step(kind, 0.72);
		vec2 lo = vec2(0.17, 0.26);
		vec2 hi = vec2(0.83, 0.82);
		lo = mix(lo, vec2(0.03, 0.09), curtain);
		hi = mix(hi, vec2(0.97, 0.93), curtain);
		lo = mix(lo, vec2(0.0, 0.3), ribbon);
		hi = mix(hi, vec2(1.0, 0.86), ribbon);
		lo = mix(lo, vec2(0.14, 0.05), fins);
		hi = mix(hi, vec2(0.86, 0.95), fins);

		bool shop = id.y < 0.5;                   // the ground floor: shopfronts
		if (shop) {
			lo = vec2(0.03, 0.05);
			hi = vec2(0.97, 0.62);
		}

		// Rooms of two to four windows are lit together; the lit fraction is the tower's.
		float roomWidth = 2.0 + floor(hash12(vec2(id.y, seed)) * 3.0);
		float room_i = floor(id.x / roomWidth);
		float r = hash12(vec2(room_i, id.y) + seed);
		float rs = hash12(vec2(room_i * 3.3, id.y * 1.7) + seed * 0.73);
		// Whole floors go dark (offices after hours), the rest are lit at the tower's fraction.
		float floorDark = step(0.74, hash12(vec2(id.y * 0.61, seed + 5.0)));
		float lit = step(r, clamp(v_custom.z * 0.62 * lit_scale, 0.0, 1.0)) * (1.0 - floorDark);
		float tint = hash12(vec2(room_i * 1.7, id.y * 3.1) + seed * 0.37);
		vec3 warm = vec3(1.0, 0.66, 0.38);
		vec3 cool = vec3(0.72, 0.86, 1.0);
		vec3 colour = tint < 0.6 ? warm : (tint < 0.95 ? cool : neon(tint * 5.0 + hue));
		float bright = mix(0.22, 1.0, pow(hash12(vec2(room_i, id.y) * 1.3 + seed), 2.0));
		// A few windows flicker, a few more show a television's blue.
		float fl = hash12(vec2(id.y, room_i) * 1.9 + seed);
		float tv = step(0.93, fl) * (0.6 + 0.4 * sin(TIME * 9.0 + fl * 50.0) * sin(TIME * 3.7 + id.x));
		colour = mix(colour, vec3(0.3, 0.5, 1.0), step(0.93, fl));
		bright *= mix(1.0, tv, step(0.93, fl));
		bright *= 1.0 - step(0.992, fl) * step(0.5, fract(sin(TIME * 23.0 + id.x * 7.0) * 43.0));
		if (shop) {
			room_i = floor(id.x / 3.0);
			lit = step(0.12, hash12(vec2(room_i * 0.37, seed)));
			colour = mix(vec3(1.0, 0.78, 0.55), neon(hash12(vec2(room_i, seed)) + hue), 0.35);
			bright = 1.15;
		}

		// How close this is: the pane's frame, rooms and wall details only exist above a few pixels a window.
		float fine = clamp(1.6 - max(aa.x, aa.y) * 9.0, 0.0, 1.0) * detail;
		float glass = 0.0;
		vec3 inside = colour * bright;
		vec3 wall_glow = vec3(0.0);
		vec3 V = normalize(CAMERA_POSITION_WORLD - v_world);

		vec2 pane = f;
		float reveal = 0.0;
		if (fine > 0.0) {
			// The wall's own axes (UV.x may run either way round it) and the ray in metres.
			vec3 T = normalize(cross(vec3(0.0, 1.0, 0.0), n));
			float along = dot(T, dFdx(v_world)) * dFdx(UV.x) + dot(T, dFdy(v_world)) * dFdy(UV.x);
			T *= along < 0.0 ? -1.0 : 1.0;
			vec3 d = vec3(-dot(V, T), -V.y, -max(dot(V, n), 0.04));
			float recess = mix(0.32, 0.1, curtain);
			vec2 slide = d.xy / -d.z * recess;
			pane = f + slide / vec2(column_m, floor_h);
			reveal = recess;
			vec3 p = vec3((id.x + pane.x) * column_m, (id.y + pane.y) * floor_h, 0.0);
			float cells = shop ? 3.0 : roomWidth;
			float x0 = room_i * cells * column_m;
			float x1 = x0 + cells * column_m;
			float y0 = id.y * floor_h;
			float y1 = y0 + floor_h * 0.86;
			float depth = shop ? 4.0 : 5.0 + 2.5 * rs;
			vec3 interior = room(p, d, x0, x1, y0, y1, depth, rs);
			if (shop) {
				// Shelves of goods along the back: rows of small coloured blocks.
				interior *= 0.7 + 0.6 * value_noise(vec2(p.x * 3.0, p.y * 7.0));
			} else {
				// Blinds or curtains close to the glass on many windows, down to a different height in each.
				float bl = hash12(vec2(id.x * 2.3, id.y) + seed);
				float drop = step(0.45, bl) * mix(0.2, 0.95, fract(bl * 7.0));
				float slats = 0.72 + 0.28 * step(0.5, fract(pane.y * floor_h * 13.0));
				float covered = step(1.0 - drop, pane.y);
				interior = mix(interior, mix(vec3(0.82, 0.78, 0.7), colour, 0.5) * 0.55 * slats, covered);
			}

			inside = mix(inside, interior * colour * (0.35 + bright) * 0.95, fine);
		}

		vec2 aw = max(aa, vec2(1e-3));
		float wx = smoothstep(lo.x - aw.x, lo.x + aw.x, pane.x) * (1.0 - smoothstep(hi.x - aw.x, hi.x + aw.x, pane.x));
		float wy = smoothstep(lo.y - aw.y, lo.y + aw.y, pane.y) * (1.0 - smoothstep(hi.y - aw.y, hi.y + aw.y, pane.y));
		glass = wx * wy;

		// The opening in the wall, unshifted: between it and the shifted pane the reveal shows.
		float ox = smoothstep(lo.x - aw.x, lo.x + aw.x, f.x) * (1.0 - smoothstep(hi.x - aw.x, hi.x + aw.x, f.x));
		float oy = smoothstep(lo.y - aw.y, lo.y + aw.y, f.y) * (1.0 - smoothstep(hi.y - aw.y, hi.y + aw.y, f.y));
		float opening = ox * oy;
		glass *= opening;

		// Mullions split the pane: a centre bar on wide windows, a transom under the top light.
		if (fine > 0.0) {
			float wide = step(2.2, column_m * (hi.x - lo.x));
			float bar = wide * (1.0 - smoothstep(0.006, 0.006 + aw.x, abs(pane.x - 0.5)));
			float transom = (1.0 - smoothstep(0.008, 0.008 + aw.y, abs(pane.y - 0.72)));
			glass *= 1.0 - max(bar, transom) * fine;
		}

		vec3 emission = shop ? inside * lit * glass * window_energy * 1.4 : inside * lit * glass * window_energy;

		// Wet glass: dark panes mirror the city, lit ones a little.
		vec3 R = reflect(-V, n);
		float fres = 0.05 + 0.75 * pow(1.0 - max(dot(V, n), 0.0), 4.0);
		vec3 mirror = glass_reflection(normalize(R + vec3(0.0, 0.0, 0.012 * sin(v_world.y * 2.3 + UV.x))));
		emission += mirror * fres * glass * mix(1.0, 0.45, lit) * (0.35 + 0.65 * detail);

		// The wall: what it is made of, weathered by the rain, in the city's light.
		float streaks = value_noise(vec2(UV.x * 0.9 + seed, height * 0.035));
		float grime = 0.62 + 0.38 * smoothstep(0.25, 0.7, streaks);
		float patch = 0.86 + 0.14 * step(0.5, hash12(vec2(floor(UV.x / bay), floor(id.y / 3.0)) + seed));
		vec3 stuff = kind < 0.3 ? vec3(0.05, 0.056, 0.068) : (kind < 0.72 ? vec3(0.13, 0.125, 0.12) : vec3(0.1, 0.066, 0.056));
		stuff = mix(stuff, stuff * vec3(1.05, 1.0, 0.9) * 0.8, fract(style * 19.0));
		vec3 albedo = stuff * grime * patch;
		// Joints: floor lines and panel joints, the shadow under each sill.
		float joint = 0.0;
		if (fine > 0.0) {
			float floorLine = 1.0 - smoothstep(0.0, 0.02 + aw.y, min(f.y, 1.0 - f.y));
			float panelLine = (1.0 - smoothstep(0.0, 0.015 + aw.x, min(f.x, 1.0 - f.x))) * (1.0 - ribbon);
			float sill = (1.0 - smoothstep(0.0, 0.05, lo.y - f.y)) * step(f.y, lo.y) * ox;
			joint = max(max(floorLine, panelLine) * 0.6, sill * 0.75) * fine;
		}

		albedo *= 1.0 - joint;
		float wall_fine = 1.0 - opening;
		// Inside the reveal the wall is lit by the room when the room is lit.
		float in_reveal = opening * (1.0 - glass) * step(0.001, reveal);
		emission += inside * lit * in_reveal * 0.5;
		wall_glow = albedo * city_light(n, height, v_world) * 2.2;

		// Colour washing the walls from signs and screens: soft pools at the low floors.
		float pool = smoothstep(0.58, 0.82, value_noise(vec2(UV.x * 0.05 + seed, height * 0.06)));
		wall_glow += albedo * neon(hash12(vec2(floor(UV.x * 0.05 + seed), 2.0)) + hue) * pool * exp(-height / 75.0) * 1.8;
		emission += wall_glow * (wall_fine + in_reveal * 0.3);

		// Fascia over the shops: lit letters on dark, one sign per shop, the shop's own colour.
		if (shop) {
			float fy = smoothstep(0.66, 0.68, f.y) * (1.0 - smoothstep(0.93, 0.95, f.y));
			vec2 letters = vec2((UV.x / column) * 2.6, f.y * 7.0);
			vec2 li = floor(letters);
			float letter = step(0.42, hash12(li + vec2(room_i, seed))) * step(0.12, fract(letters.x)) * step(0.1, fract(letters.y));
			float shopOn = step(0.15, hash12(vec2(room_i * 1.3, seed)));
			vec3 fc = neon(hash12(vec2(room_i * 0.7, seed)) + hue);
			emission = mix(emission, fc * (0.18 + 2.2 * letter * fine + 0.6 * (1.0 - fine)) * shopOn, fy);
		}

		// Accent bands: some towers have a neon line every few storeys.
		float every = floor(mix(8.0, 20.0, fract(style * 3.7)));
		float bandOn = step(0.62, fract(style * 11.0)) * step(mod(id.y, every), 0.5) * step(1.5, id.y);
		float band = bandOn * (1.0 - smoothstep(0.022, 0.022 + aw.y * 2.0, f.y));
		emission += accent * band * trim_energy * neon_scale * 0.5;

		// Far away the grid is smaller than a pixel: fade to its average so it does not shimmer, keeping the rough pattern
		// of lit and dark floors.
		float blur = clamp(max(aa.x, aa.y) * 1.6 - 0.35, 0.0, 1.0);
		float area = (hi.x - lo.x) * (hi.y - lo.y);
		float clump = 0.45 + 1.1 * hash12(floor(vec2(UV.x / 14.0, cell.y / 3.0)) + seed);
		vec3 average = mix(warm, cool, 0.4) * clamp(v_custom.z * lit_scale, 0.0, 1.0) * clump * area * 0.62 * 0.74 * 0.5 * window_energy * 0.9
			+ accent * trim_energy * neon_scale * 0.5 * bandOn * 0.05 + albedo * city_light(n, height, v_world) * 1.6 * (1.0 - area);
		EMISSION = mix(emission, average, blur);

		float glassAmount = mix(glass, area, blur);
		ALBEDO = mix(albedo, vec3(0.008, 0.01, 0.016), glassAmount);
		ROUGHNESS = mix(0.62, 0.1, glassAmount);
		METALLIC = mix(0.05, 0.5, glassAmount);
	}
}
