shader_type canvas_item;
// Render-test canvas shader: a vertex wobble from MODEL_MATRIX world space, a uniform colour array, a sampler uniform
// and TIME/FRAGCOORD/UV built-ins (docs/design/canvas.md, canvas shaders).
uniform vec4 tints[2] : source_color;
uniform float stripes = 6.0;
uniform sampler2D mask : filter_nearest, repeat_disable;
varying vec2 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
	VERTEX.y += sin(VERTEX.x * 0.5) * 2.0;
}

void fragment() {
	float band = step(0.5, fract(UV.x * stripes));
	vec4 tint = mix(tints[0], tints[1], band);
	float m = texture(mask, UV).a;
	COLOR = vec4(tint.rgb * (0.75 + 0.25 * fract(v_world.y / 40.0)), m * COLOR.a);
}
