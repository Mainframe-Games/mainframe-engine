shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;
// Neon's giant holograms (World/NeonCity.cs): additive light with scanlines, a rim, flicker and the odd glitch. `shape`:
// 0 a koi (the kit's Holo_Koi, 1 m long: it swims a circle of `orbit` local metres round the node and wiggles), 1 an
// emblem (Holo_Logo: it turns about +Y), 2 a dancer (a unit quad, UV 0..1: a figure drawn as distance-field capsules).
uniform float shape = 0.0;
uniform vec4 color : source_color = vec4(0.1, 0.9, 1.0, 1.0);
uniform vec4 color2 : source_color = vec4(1.0, 0.25, 0.7, 1.0);
uniform float energy = 3.0;
uniform float orbit = 1.2;
uniform float speed = 0.12;
uniform float phase = 0.0;
uniform float line_spacing = 0.6;

varying vec3 v_world;
varying vec3 v_local;

float hash11(float p) {
	p = fract(p * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

float capsule(vec2 p, vec2 a, vec2 b, float r) {
	vec2 pa = p - a;
	vec2 ba = b - a;
	float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
	return length(pa - ba * h) - r;
}

vec2 limb(vec2 from, float angle, float len) {
	return from + vec2(sin(angle), -cos(angle)) * len;
}

// The dancer: a figure of capsules, its joints swinging to a beat.
float dancer(vec2 p, float t) {
	float beat = t * 2.2;
	vec2 hip = vec2(0.5 + 0.03 * sin(beat), 0.42 + 0.015 * abs(sin(beat)));
	vec2 neck = hip + vec2(0.02 * sin(beat + 0.5), 0.22);
	float d = capsule(p, hip, neck, 0.035);
	d = min(d, length(p - (neck + vec2(0.01 * sin(beat), 0.07))) - 0.05);
	// arms: shoulder -> elbow -> hand, mirrored and out of phase
	for (int s = 0; s < 2; s++) {
		float side = s == 0 ? -1.0 : 1.0;
		vec2 shoulder = neck + vec2(side * 0.06, -0.02);
		float a1 = side * (1.9 + 0.9 * sin(beat + side * 1.2));
		vec2 elbow = limb(shoulder, a1, 0.12);
		vec2 hand = limb(elbow, a1 + side * (0.6 + 0.7 * sin(beat * 2.0 + side)), 0.11);
		d = min(d, capsule(p, shoulder, elbow, 0.022));
		d = min(d, capsule(p, elbow, hand, 0.018));
		// legs: hip -> knee -> foot
		vec2 h2 = hip + vec2(side * 0.04, 0.0);
		float a2 = side * (0.15 + 0.25 * max(0.0, sin(beat + (side > 0.0 ? 0.0 : 3.14))));
		vec2 knee = limb(h2, a2, 0.17);
		vec2 foot = limb(knee, a2 * 0.3 - side * 0.05, 0.17);
		d = min(d, capsule(p, h2, knee, 0.03));
		d = min(d, capsule(p, knee, foot, 0.024));
	}
	return d;
}

void vertex() {
	vec3 v = VERTEX;
	vec3 n = NORMAL;
	if (shape < 0.5) {
		// Swim: a wave down the body, stronger towards the tail (+Z); then round the circle, head first.
		float tail = clamp(v.z + 0.5, 0.0, 1.0);
		v.x += sin(v.z * 7.0 - TIME * 3.0) * 0.07 * tail * tail;
		float a = TIME * speed + phase;
		v += vec3(orbit, 0.12 * sin(TIME * 0.4), 0.0);
		float c = cos(a);
		float s = sin(a);
		v = vec3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
		n = vec3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z);
	} else if (shape < 1.5) {
		float a = TIME * speed;
		float c = cos(a);
		float s = sin(a);
		v = vec3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
		n = vec3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z);
	}
	VERTEX = v;
	NORMAL = n;
	v_local = VERTEX;
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Glitch: now and then the hologram jumps and dims for a few frames.
	float tick = floor(TIME * 7.0);
	float glitch = step(0.94, hash11(tick + shape * 13.0));
	float flicker = (0.88 + 0.12 * sin(TIME * 41.0)) * (1.0 - 0.6 * glitch);
	float scan = 0.55 + 0.45 * sin((v_world.y + glitch * 0.7) / line_spacing * 6.2832 - TIME * 3.0);
	vec3 col = color.rgb;
	float alpha = 1.0;

	if (shape > 1.5) {
		vec2 p = vec2(UV.x, 1.0 - UV.y);
		p.x += glitch * 0.02 * sin(p.y * 80.0);
		float d = dancer(p, TIME);
		float body = 1.0 - smoothstep(0.0, 0.006, d);
		float halo = exp(-max(d, 0.0) * 40.0) * 0.35;
		col = mix(color2.rgb, color.rgb, p.y);
		alpha = body * 0.6 + halo + (1.0 - smoothstep(0.004, 0.012, abs(d))) * 0.8;
		// a ring of light on the roof under the dancer
		alpha += (1.0 - smoothstep(0.0, 0.01, abs(length((p - vec2(0.5, 0.06)) * vec2(1.0, 6.0)) - 0.18))) * 0.6;
	} else {
		float rim = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), 2.0);
		if (shape < 0.5) {
			// koi markings: patches of the second colour
			float spots = step(0.55, sin(v_local.z * 23.0 + 1.3) * sin(v_local.x * 31.0 + v_local.y * 17.0));
			col = mix(color.rgb, color2.rgb, spots);
		}
		alpha = 0.25 + rim * 1.4;
	}
	ALBEDO = vec3(0.0);
	EMISSION = col * energy * alpha * scan * flicker;
}
