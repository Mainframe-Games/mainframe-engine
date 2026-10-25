shader_type spatial;
// The dissolve of a felled prop (ADR 0205): object-space noise against `progress` cuts the surface away
// (ALPHA_SCISSOR_THRESHOLD: an alpha-tested cutout, in its shadow and in picking too) with a glowing edge; `hit_flash`
// tints the surface for a moment when the prop is struck (its alpha is the strength).
uniform float progress : hint_range(0.0, 1.0) = 0.3;
uniform float edge_width = 0.07;
uniform vec4 base_color : source_color = vec4(0.42, 0.55, 0.72, 1.0);
uniform vec3 edge_color : source_color = vec3(1.0, 0.5, 0.12);
uniform float edge_energy = 5.0;
uniform vec4 hit_flash : source_color = vec4(1.0, 0.35, 0.3, 0.0);
varying vec3 v_local;

float hash3(vec3 p) {
	return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
}

float noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);
	float a = mix(mix(hash3(i), hash3(i + vec3(1.0, 0.0, 0.0)), u.x), mix(hash3(i + vec3(0.0, 1.0, 0.0)), hash3(i + vec3(1.0, 1.0, 0.0)), u.x), u.y);
	float b = mix(mix(hash3(i + vec3(0.0, 0.0, 1.0)), hash3(i + vec3(1.0, 0.0, 1.0)), u.x), mix(hash3(i + vec3(0.0, 1.0, 1.0)), hash3(i + vec3(1.0, 1.0, 1.0)), u.x), u.y);
	return mix(a, b, u.z);
}

void vertex() {
	v_local = VERTEX;
}

void fragment() {
	float n = noise3(v_local * 3.0) * 0.65 + noise3(v_local * 8.0) * 0.35;
	float edge = 1.0 - smoothstep(0.0, edge_width, n - progress);
	ALBEDO = mix(base_color.rgb, hit_flash.rgb, hit_flash.a);
	EMISSION = edge_color * edge_energy * edge * step(0.001, progress) + hit_flash.rgb * hit_flash.a * 0.6;
	ROUGHNESS = 0.45;
	METALLIC = 0.15;
	ALPHA = n;
	ALPHA_SCISSOR_THRESHOLD = progress;
}
