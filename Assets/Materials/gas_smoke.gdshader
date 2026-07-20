shader_type canvas_item;

// Shared "liquid gas" distortion for dust/smoke particles project-wide —
// same "sample noise, use it to offset the UV lookup" technique
// laser.gdshader already uses for its energy-beam wobble, tuned here for a
// slow turbulent curl instead so smoke visibly warps/drifts per-particle
// rather than just fading as a flat raster sprite. First pass applied to
// Sam's jump/land/slide dust and the laser's impact smoke — since swept to
// every dust/smoke/spark site project-wide.
//
// Also doubles as the project's shared "randomize which texture variant a
// particle uses" shader: when h_frames/v_frames > 1, TEXTURE is treated as
// a grid sprite-sheet and each particle samples a single cell, picked once
// at spawn via that CPUParticles2D's anim_offset_min/max (with anim_speed
// left at 0 so it never animates through the sheet — it just lands on one
// cell and stays there for its whole lifetime). Godot feeds that per-particle
// anim_offset into INSTANCE_CUSTOM.x automatically even under a fully custom
// shader like this one, so no CanvasItemMaterial particle-animation setup is
// needed on top of this. Left at the 1x1 default this is a no-op — existing
// single-texture users are unaffected. Reused for sparks too, typically with
// distortion_strength set near 0 there since sparks want the texture-variety
// half of this without the smoky warp.

uniform sampler2D noise_texture : repeat_enable, filter_linear;
uniform float distortion_strength : hint_range(0.0, 0.5) = 0.12;
uniform float distortion_scale = 1.5;
uniform float drift_speed = 0.12;
uniform float turbulence_speed = 0.35;
uniform int h_frames : hint_range(1, 8) = 1;
uniform int v_frames : hint_range(1, 8) = 1;

// INSTANCE_CUSTOM is only readable in vertex() for canvas_item shaders, not
// fragment() — has to be carried over through a varying.
varying float v_anim_offset;

void vertex() {
	v_anim_offset = INSTANCE_CUSTOM.x;
}

void fragment() {
	vec2 uv = UV;

	// Two noise samples panning in different directions/speeds — the
	// combined warp reads as slow swirling turbulence instead of a single
	// wave sliding one way, which is what made the old plain-texture
	// particles read as straight-drifting raster sprites rather than gas.
	vec2 warp_uv_a = uv * distortion_scale + vec2(TIME * drift_speed, -TIME * drift_speed * 0.6);
	vec2 warp_uv_b = uv * distortion_scale * 1.7 + vec2(-TIME * turbulence_speed * 0.5, TIME * turbulence_speed);

	vec2 warp_a = texture(noise_texture, warp_uv_a).rg - 0.5;
	vec2 warp_b = texture(noise_texture, warp_uv_b).rg - 0.5;

	vec2 offset = (warp_a + warp_b) * distortion_strength;
	// Clamped rather than wrapped — this UV is about to be remapped into a
	// single cell of a (possibly multi-frame) sheet below, and wrapping
	// could sample past the cell's edge into a neighboring frame.
	vec2 warped_uv = clamp(uv + offset, 0.0, 1.0);

	int total_frames = h_frames * v_frames;
	if (total_frames > 1) {
		int frame = clamp(int(v_anim_offset * float(total_frames)), 0, total_frames - 1);
		vec2 cell_size = vec2(1.0 / float(h_frames), 1.0 / float(v_frames));
		vec2 cell_origin = vec2(float(frame % h_frames), float(frame / h_frames)) * cell_size;
		warped_uv = cell_origin + warped_uv * cell_size;
	}

	vec4 tex_color = texture(TEXTURE, warped_uv);

	COLOR = tex_color * COLOR;
}
