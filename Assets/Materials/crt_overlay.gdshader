shader_type canvas_item;

// CCTV/CRT overlay — a visible green-phosphor monitor tint with crisp,
// pixel-spaced scanlines, a vignette, sensor grain, and per-line static.
// The first version of this shader only ever darkened (pure black alpha),
// and scanline spacing was defined in UV space (cycles per 0..1), which at
// typical window heights aliased into a near-uniform ~17% dim instead of
// visible bands — against the menus' already-dark navy background that
// read as no effect at all. This version applies an actual color tint so
// the effect can't be missed.
//
// Scanline/static math is driven by FRAGCOORD (true framebuffer pixels)
// rather than UV/SCREEN_PIXEL_SIZE — the old UV-based math silently assumed
// this shader's ColorRect always spans the full viewport, so scanline
// spacing would come out wrong (stretched/compressed) on any smaller rect.
// FRAGCOORD is correct regardless of how big or small the rect this runs on
// is, which is what lets this same material also work on something as
// small as SamHUD's corner panel, not just a full-screen menu overlay.
//
// The old "flicker" was a smooth sine-wave brightness pulse across the
// WHOLE screen — reads as an unnatural uniform breathing rather than an
// actual CRT artifact. Real interference reads as individual scanlines
// randomly jittering brighter/darker, changing abruptly rather than
// smoothly animating — that's what static_amount below does instead.

uniform vec4 tint_color : source_color = vec4(0.65, 1.0, 0.75, 1.0);
uniform float tint_amount : hint_range(0.0, 1.0) = 0.16;
uniform float scanline_intensity : hint_range(0.0, 1.0) = 0.5;
uniform float scanline_spacing_px = 3.0;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.55;
uniform float noise_amount : hint_range(0.0, 0.3) = 0.05;
// Per-scanline static jitter amount/reseed-rate — same uniform names kept
// as "flicker_*" so every existing scene using this shader picks up the new
// static behavior with zero changes needed to their saved parameter values.
uniform float flicker_amount : hint_range(0.0, 0.3) = 0.05;
uniform float flicker_speed = 5.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec2 uv = UV;
	vec2 screen_px = FRAGCOORD.xy;

	// Hard-edged alternating bands, spaced in real screen pixels so they
	// read as crisp discrete scanlines regardless of window resolution.
	float line = mod(screen_px.y, scanline_spacing_px * 2.0);
	float scanline_dark = (line < scanline_spacing_px) ? scanline_intensity : 0.0;

	vec2 centered = uv * 2.0 - 1.0;
	float vignette_dark = smoothstep(0.15, 1.0, length(centered)) * vignette_strength;

	// Grain re-rolls every frame via TIME so it reads as live sensor noise.
	float grain = hash(uv * 800.0 + vec2(TIME * 60.0, 0.0));
	float noise_dark = (0.5 - grain) * noise_amount;

	// Static: each individual scanline band gets its own random brightness
	// jitter, reseeded a handful of times per second (flicker_speed, still
	// in roughly-Hz terms) rather than every frame — that's what makes it
	// read as discrete flickering static instead of either a smooth pulse
	// or continuous shimmering grain.
	float band_index = floor(screen_px.y / scanline_spacing_px);
	float static_seed = floor(TIME * flicker_speed);
	float static_noise = hash(vec2(band_index, static_seed));
	float flicker_dark = (static_noise - 0.5) * flicker_amount * 2.0;

	float dark = clamp(scanline_dark + vignette_dark + noise_dark + flicker_dark, 0.0, 1.0);

	// Scanlines/vignette/noise darken the tint itself (darker green bands)
	// while also boosting alpha, instead of fighting the base tint as a
	// separate blend pass.
	vec3 color = tint_color.rgb * (1.0 - dark * 0.85);
	float alpha = clamp(tint_amount + dark * 0.6, 0.0, 1.0);

	COLOR = vec4(color, alpha);
}
