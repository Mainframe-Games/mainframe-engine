shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled;
// Neon's far skyline (World/NeonCity.cs): open cylinders round the city beyond the real towers, their inside drawn as
// procedural silhouettes with window grids, sinking into the haze. UV.x is metres round the cylinder, UV.y metres above
// the street. Each `slot` metres holds one building: its height, width, setback and antenna come from a hash.
uniform float seed = 0.0;
uniform float slot = 26.0;
uniform float min_height = 40.0;
uniform float max_height = 260.0;
uniform float haze = 0.6;
uniform vec4 haze_color : source_color = vec4(0.28, 0.08, 0.3, 1.0);
uniform vec4 haze_low : source_color = vec4(0.5, 0.12, 0.38, 1.0);
uniform float window_energy = 1.2;

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

void fragment() {
	float x = UV.x / slot;
	float id = floor(x) + seed * 1000.0;
	float f = fract(x);
	float y = UV.y;

	float h1 = hash11(id * 1.37);
	float height = mix(min_height, max_height, h1 * h1 * h1);
	float left = 0.04 + 0.22 * hash11(id * 2.11);
	float right = 0.96 - 0.22 * hash11(id * 3.71);
	// A setback: the top third narrower.
	float setback = step(0.5, hash11(id * 5.3)) * step(height * 0.68, y);
	left = mix(left, mix(left, right, 0.25), setback);
	right = mix(right, mix(left, right, 0.75), setback);
	// A second, lower building in front of the slot's right half.
	float front = step(0.55, hash11(id * 7.9)) * step(y, height * 0.45) * step(0.55, f);
	float inside = step(left, f) * step(f, right) * step(y, height) + front;
	// An antenna on the tall ones.
	float antenna = step(0.7, h1) * step(abs(f - (left + right) * 0.5), 0.012) * step(y, height + 30.0 * h1);
	if (inside + antenna < 0.5) {
		discard;
	}

	// Windows: a grid on the building, a lit fraction per building.
	vec2 cell = vec2(UV.x / 2.6, y / 3.5);
	vec2 cid = floor(cell);
	vec2 cf = fract(cell);
	float window = step(0.2, cf.x) * step(cf.x, 0.8) * step(0.25, cf.y) * step(cf.y, 0.8);
	float litFraction = mix(0.15, 0.6, hash11(id * 11.3));
	float lit = step(hash12(cid + id), litFraction);
	vec2 aa = fwidth(cell);
	float blur = clamp(max(aa.x, aa.y) * 1.5 - 0.3, 0.0, 1.0);
	float glow = mix(window * lit, litFraction * 0.36, blur);
	float tint = hash12(cid * 0.7 + id);
	vec3 colour = tint < 0.6 ? vec3(1.0, 0.62, 0.32) : (tint < 0.9 ? vec3(0.7, 0.85, 1.0) : vec3(1.0, 0.2, 0.7));
	// A neon crown on some.
	float crown = step(0.6, hash11(id * 13.1)) * step(abs(y - height + 1.5), 0.8) * inside;
	vec3 crownColour = hash11(id * 17.0) < 0.5 ? vec3(1.0, 0.15, 0.6) : vec3(0.1, 0.8, 1.0);

	// Haze: thicker towards the street, where the city's light fills it.
	float low = exp(-y / 70.0);
	float amount = clamp(haze + (1.0 - haze) * low * 0.85, 0.0, 1.0);
	vec3 hazeColour = mix(haze_color.rgb, haze_low.rgb, low);
	vec3 silhouette = vec3(0.006, 0.006, 0.012);
	ALBEDO = mix(silhouette, hazeColour, amount);
	EMISSION = (colour * glow * window_energy * inside + crownColour * crown * 3.0 + vec3(1.0, 0.05, 0.02) * antenna * step(height + 28.0 * h1, y) * 6.0) * (1.0 - amount * 0.8);
}
