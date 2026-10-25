shader_type spatial;
render_mode cull_disabled, specular_blinn;
// Fungal stalks swaying in the wind (ADR 0205): vertex() moves each vertex by its world position and TIME, more towards
// the tip (UV.y = 0 at the top); every MultiMesh instance is out of step by INSTANCE_CUSTOM.x and takes its cap colour from
// INSTANCE_CUSTOM.yzw. The shadow pass runs the same vertex(), so the shadows sway too.
uniform vec3 stalk_color : source_color = vec3(0.86, 0.80, 0.70);
uniform float sway = 0.4;
uniform float speed = 1.4;
varying vec3 v_cap;

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float bend = (1.0 - UV.y) * (1.0 - UV.y);
	float phase = INSTANCE_CUSTOM.x * TAU + world.x * 0.4 + world.z * 0.3;
	VERTEX.x += sin(TIME * speed + phase) * sway * bend;
	VERTEX.z += cos(TIME * speed * 0.7 + phase) * sway * 0.5 * bend;
	v_cap = INSTANCE_CUSTOM.yzw;
}

void fragment() {
	float cap = smoothstep(0.22, 0.12, UV.y);
	ALBEDO = mix(stalk_color, v_cap, cap);
	EMISSION = v_cap * cap * 0.35;
	ROUGHNESS = 0.55;
}
