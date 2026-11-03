shader_type spatial;
// The crowd on the pavements (World/NeonCity.cs): a MultiMesh of one kit walker, part 0 the body and part 1 the umbrella.
// Nobody is a node: INSTANCE_CUSTOM is (phase, hue, span, speed) and the shader walks each one along its pavement, +Z in
// the instance's space, starting again at the far end of the span metres.
uniform float part = 0.0;

varying flat vec3 v_custom;

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
	float span = INSTANCE_CUSTOM.z;
	float speed = INSTANCE_CUSTOM.w;
	float walked = mod(TIME * speed + INSTANCE_CUSTOM.x * span, span);
	VERTEX.z += walked;
	VERTEX.y += 0.03 * abs(sin(TIME * speed * 4.0 + INSTANCE_CUSTOM.x * 40.0));
	// Anyone at the end of the span shrinks away rather than pop.
	float edge = smoothstep(0.0, 1.5, walked) * (1.0 - smoothstep(span - 1.5, span, walked));
	VERTEX.xz = mix(vec2(0.0, VERTEX.z), VERTEX.xz, edge);
	v_custom = INSTANCE_CUSTOM.xyz;
}

void fragment() {
	if (part > 0.5) {
		// Most umbrellas are black or clear; one in eight glows, a lit shaft in its handle being the fashion.
		float glow = step(0.875, hash11(v_custom.x * 91.0));
		ALBEDO = mix(vec3(0.03), vec3(0.2), step(0.5, hash11(v_custom.x * 37.0)));
		ROUGHNESS = 0.2;
		EMISSION = neon(v_custom.y) * glow * 0.8 + ALBEDO * vec3(0.5, 0.3, 0.5);
	} else {
		ALBEDO = vec3(0.03, 0.03, 0.035);
		EMISSION = vec3(0.05, 0.02, 0.05);
	}
}
