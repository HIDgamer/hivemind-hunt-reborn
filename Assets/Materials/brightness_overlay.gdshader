shader_type canvas_item;

// Global screen brightness correction — multiplies the already-fully-
// rendered frame (everything: gameplay, HUD, CRT overlays, menus) rather
// than blending a translucent white/black rect on top, which would wash out
// or muddy colors instead of genuinely brightening/darkening them.
uniform float brightness : hint_range(0.5, 1.5) = 1.0;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;

void fragment() {
	vec4 col = texture(screen_tex, SCREEN_UV);
	COLOR = vec4(col.rgb * brightness, col.a);
}
