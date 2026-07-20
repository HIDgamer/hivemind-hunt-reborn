shader_type canvas_item;

// ── Color ─────────────────────────────────────────────────────────────────────
uniform vec4 laser_color : source_color = vec4(0.50, 0.02, 0.01, 1.0); // Dark crimson outer glow
uniform vec4 mid_color : source_color = vec4(0.85, 0.12, 0.06, 1.0);   // Warm red mid-glow
uniform vec4 core_color : source_color = vec4(1.00, 0.78, 0.72, 1.0);  // White-hot pink core

// ── Animation ─────────────────────────────────────────────────────────────────
uniform float scroll_speed = 3.5;   // Energy flow speed along beam
uniform float pulse_speed = 3.5;    // Hz – keep low to avoid aliasing at 60fps
uniform float pulse_amount = 0.07;  // Amplitude of pulse (subtle)

// ── Distortion ────────────────────────────────────────────────────────────────
uniform float distortion_strength = 0.055; // Reduced: too high causes visual tearing
uniform float distortion_scale = 1.20;

// ── Shape ─────────────────────────────────────────────────────────────────────
uniform float core_sharpness = 7.0;  // Higher = narrower white core
uniform float edge_fade_power = 3.0; // Steeper falloff = crisper lightsaber edge

// ── State ─────────────────────────────────────────────────────────────────────
// Driven by C# each frame: 0.0 = fully off, 1.0 = fully charged
uniform float charge_level : hint_range(0.0, 1.0) = 1.0;

uniform sampler2D noise_texture : repeat_enable, filter_linear;

void fragment() {
    vec2 uv = UV;

    // ── Slow distortion pass ──────────────────────────────────────────────────
    // Gentle wobble so the beam feels alive; keep speed low to avoid jitter.
    vec2 dist_uv = vec2(uv.x * distortion_scale + TIME * scroll_speed * 0.18, uv.y * 0.8);
    float distort = texture(noise_texture, dist_uv).r - 0.5;
    float disp_y = uv.y + distort * distortion_strength * charge_level;

    // ── Energy flow noise ─────────────────────────────────────────────────────
    // Two layers at different frequencies give depth without harsh strobing.
    vec2 flow_a = vec2(uv.x + TIME * scroll_speed, disp_y * 1.25);
    vec2 flow_b = vec2(uv.x * 0.6 + TIME * scroll_speed * 0.5, disp_y * 0.7 + 0.38);
    float noise_a = texture(noise_texture, flow_a).r;
    float noise_b = texture(noise_texture, flow_b).r;
    float energy = mix(noise_a, noise_b, 0.40);

    // ── Radial profile ────────────────────────────────────────────────────────
    // dist: 0 at centreline, 1 at outer edge
    float dist = clamp(abs(disp_y - 0.5) * 2.0, 0.0, 1.0);

    // Outer beam — steep power gives a hard-edged lightsaber silhouette
    float beam = pow(clamp(1.0 - dist, 0.0, 1.0), edge_fade_power);

    // Mid-glow — warm zone between core and outer edge. Narrower falloff
    // than before (2.4/2.2 vs the old 1.8/1.6) so it reads as a distinct
    // band instead of covering most of the beam's width.
    float mid = pow(clamp(1.0 - dist * 2.4, 0.0, 1.0), 2.2);

    // Core — narrow, overdriven white-hot streak
    float core = pow(clamp(1.0 - dist * core_sharpness, 0.0, 1.0), 2.5);

    // ── Smooth animation (NO high-frequency flicker) ──────────────────────────
    // Pulse: gentle, low-frequency brightness undulation
    float pulse = sin(TIME * pulse_speed) * pulse_amount + (1.0 - pulse_amount);

    // Energy rings: bands that scroll along the beam like a plasma current.
    // Frequency chosen so rings are visible but don't alias at 60fps.
    float rings = sin(uv.x * 14.0 - TIME * scroll_speed * 5.0) * 0.035 + 0.965;

    float anim = pulse * rings;

    // ── Colour layers (outer → mid → core) ───────────────────────────────────
    // Each layer is just its own modulated color now — no longer pre-masked
    // and summed, since two masked colors ADDED together meant whichever
    // one was brighter (mid_color) visually swallowed the other across most
    // of the beam's width instead of reading as three separate bands. mix()
    // picks exactly one color at each point across the radial profile, so
    // outer/mid/core stay visually distinct band-to-band.
    vec3 outer_rgb = laser_color.rgb * (0.55 + energy * 0.45) * anim;
    vec3 mid_rgb = mid_color.rgb * (0.55 + energy * 0.45) * anim;
    vec3 core_rgb = core_color.rgb * (1.15 + energy * 0.20) * pulse;

    vec3 rgb = mix(outer_rgb, mid_rgb, mid) * beam;
    rgb = mix(rgb, core_rgb, core);

    // ── Alpha ─────────────────────────────────────────────────────────────────
    // Outer alpha shaped by beam mask, noise, pulse, and charge
    float alpha = beam * (0.45 + energy * 0.55) * anim * charge_level;
    // Core is nearly opaque when fully charged
    alpha = mix(alpha, charge_level, core * 0.93);

    // Clamp rgb to 1.5 — enough headroom for bloom without causing glitch on LDR
    COLOR = vec4(clamp(rgb, 0.0, 1.5), clamp(alpha, 0.0, 1.0));
}