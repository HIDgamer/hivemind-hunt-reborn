shader_type canvas_item;

// Volumetric-look ambient gas/fog/smoke haze. Two independently-scrolling
// layers of tileable noise are blended together and masked by a soft radial
// falloff, so a single flat rect reads as a drifting cloud volume instead of
// a sprinkle of individual particle dots or a hard-edged smoke card.

uniform sampler2D noise_texture : repeat_enable, filter_linear;
uniform vec4 gas_color : source_color = vec4(0.55, 0.65, 0.6, 1.0);
uniform float density : hint_range(0.0, 1.0) = 0.45;
uniform float scroll_speed = 0.03;
uniform float scale_a = 1.0;
uniform float scale_b = 2.3;
uniform float edge_softness : hint_range(0.05, 1.0) = 0.4;

void fragment() {
	vec2 uv = UV;

	vec2 uv_a = uv * scale_a + vec2(TIME * scroll_speed, TIME * scroll_speed * 0.4);
	vec2 uv_b = uv * scale_b - vec2(TIME * scroll_speed * 0.6, TIME * scroll_speed * 0.25);

	float noise_a = texture(noise_texture, uv_a).r;
	float noise_b = texture(noise_texture, uv_b).r;
	float n = mix(noise_a, noise_b, 0.5);

	// Soft radial mask so the rect's edges fade out instead of cutting off hard.
	vec2 centered = uv * 2.0 - 1.0;
	float dist = length(centered);
	float mask = 1.0 - smoothstep(1.0 - edge_softness, 1.0, dist);

	float alpha = n * density * mask;
	COLOR = vec4(gas_color.rgb, clamp(alpha, 0.0, 1.0));
}
