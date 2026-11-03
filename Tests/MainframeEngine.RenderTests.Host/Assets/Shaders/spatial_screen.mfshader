shader_type spatial;
render_mode unshaded;
// Render-test spatial shader (ADR 0216): a screen showing a sub-viewport through a sampler uniform. The channels are
// rotated (rgb → gbr), so the test tells this shader's draw from a standard material's.
uniform sampler2D screen : source_color, filter_linear, repeat_disable;

void fragment() {
	ALBEDO = texture(screen, UV).gbr;
}
