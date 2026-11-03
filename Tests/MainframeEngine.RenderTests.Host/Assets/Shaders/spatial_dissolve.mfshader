shader_type spatial;
// Render-test spatial shader (ADR 0205): the dissolve of a felled prop. Object-space value noise against `progress`
// cuts the surface away (ALPHA_SCISSOR_THRESHOLD: an alpha-tested cutout, in the shadow and picking passes too) and an
// emissive band glows along the cut edge; `hit_flash` tints the whole surface (a uniform set from C#).
uniform float progress : hint_range(0.0, 1.0) = 0.45;
uniform float edge_width = 0.08;
uniform vec4 base_color : source_color = vec4(0.30, 0.55, 0.85, 1.0);
uniform vec3 edge_color : source_color = vec3(1.0, 0.45, 0.1);
uniform float edge_energy = 4.0;
uniform vec4 hit_flash : source_color = vec4(1.0, 1.0, 1.0, 0.0);
varying vec3 v_local;

float hash3(vec3 p) {
	return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
}

float noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);
	float n000 = hash3(i);
	float n100 = hash3(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash3(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash3(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash3(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash3(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash3(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash3(i + vec3(1.0, 1.0, 1.0));
	return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y), mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}

void vertex() {
	v_local = VERTEX;
}

void fragment() {
	float n = noise3(v_local * 3.5) * 0.65 + noise3(v_local * 9.0) * 0.35;
	float edge = 1.0 - smoothstep(0.0, edge_width, n - progress);
	ALBEDO = mix(base_color.rgb, hit_flash.rgb, hit_flash.a);
	EMISSION = edge_color * edge_energy * edge;
	ROUGHNESS = 0.5;
	METALLIC = 0.1;
	ALPHA = n;
	ALPHA_SCISSOR_THRESHOLD = progress;
}
