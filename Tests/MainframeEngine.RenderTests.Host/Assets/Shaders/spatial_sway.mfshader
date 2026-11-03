shader_type spatial;
render_mode cull_disabled, specular_blinn;
// Render-test spatial shader (ADR 0205): wind sway of fungal stalks, displaced by world position and TIME in every pass
// (the shadow follows), more towards the tip (UV.y = 0 at the top), each MultiMesh instance out of step by its
// INSTANCE_CUSTOM.x; the cap colour comes from INSTANCE_CUSTOM.yzw through a varying.
uniform vec3 stalk_color : source_color = vec3(0.85, 0.80, 0.70);
uniform float sway = 0.45;
uniform float speed = 1.3;
varying vec3 v_tint;

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float bend = (1.0 - UV.y) * (1.0 - UV.y);
	float phase = INSTANCE_CUSTOM.x * TAU + world.x * 0.4 + world.z * 0.3;
	VERTEX.x += sin(TIME * speed + phase) * sway * bend;
	VERTEX.z += cos(TIME * speed * 0.7 + phase) * sway * 0.5 * bend;
	v_tint = INSTANCE_CUSTOM.yzw;
}

void fragment() {
	float cap = smoothstep(0.25, 0.15, UV.y);
	ALBEDO = mix(stalk_color, v_tint, cap);
	ROUGHNESS = 0.6;
}
