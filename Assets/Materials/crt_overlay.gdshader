shader_type canvas_item;

// CCTV/CRT overlay — a visible green-phosphor monitor tint with crisp,
// pixel-spaced scanlines, a vignette, sensor grain, and an irregular
// flicker. The first version of this shader only ever darkened (pure black
// alpha), and scanline spacing was defined in UV space (cycles per 0..1),
// which at typical window heights aliased into a near-uniform ~17% dim
// instead of visible bands — against the menus' already-dark navy
// background that read as no effect at all. This version applies an actual
// color tint so the effect can't be missed, and spaces scanlines in real
// screen pixels so they stay crisp regardless of window resolution.

uniform vec4 tint_color : source_color = vec4(0.65, 1.0, 0.75, 1.0);
uniform float tint_amount : hint_range(0.0, 1.0) = 0.16;
uniform float scanline_intensity : hint_range(0.0, 1.0) = 0.5;
uniform float scanline_spacing_px = 3.0;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.55;
uniform float noise_amount : hint_range(0.0, 0.3) = 0.05;
uniform float flicker_amount : hint_range(0.0, 0.3) = 0.05;
uniform float flicker_speed = 5.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec2 uv = UV;
	vec2 screen_px = uv / SCREEN_PIXEL_SIZE;

	// Hard-edged alternating bands, spaced in real screen pixels so they
	// read as crisp discrete scanlines regardless of window resolution.
	float line = mod(screen_px.y, scanline_spacing_px * 2.0);
	float scanline_dark = (line < scanline_spacing_px) ? scanline_intensity : 0.0;

	vec2 centered = uv * 2.0 - 1.0;
	float vignette_dark = smoothstep(0.15, 1.0, length(centered)) * vignette_strength;

	// Grain re-rolls every frame via TIME so it reads as live sensor noise.
	float grain = hash(uv * 800.0 + vec2(TIME * 60.0, 0.0));
	float noise_dark = (0.5 - grain) * noise_amount;

	// Two off-beat sine waves instead of one so the flicker feels irregular
	// (a failing feed) rather than a clean, obviously-looping pulse.
	float flicker = sin(TIME * flicker_speed) * 0.5 + sin(TIME * flicker_speed * 2.7) * 0.5;
	float flicker_dark = flicker * flicker_amount;

	float dark = clamp(scanline_dark + vignette_dark + noise_dark + flicker_dark, 0.0, 1.0);

	// Scanlines/vignette/noise darken the tint itself (darker green bands)
	// while also boosting alpha, instead of fighting the base tint as a
	// separate blend pass.
	vec3 color = tint_color.rgb * (1.0 - dark * 0.85);
	float alpha = clamp(tint_amount + dark * 0.6, 0.0, 1.0);

	COLOR = vec4(color, alpha);
}
