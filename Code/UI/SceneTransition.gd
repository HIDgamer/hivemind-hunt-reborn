extends CanvasLayer

# Autoload — a single global "cut" for every menu-to-menu scene change.
# Swapping the SceneTree's root scene is an unavoidable hard pop; fading
# the OLD screen out and letting the NEW one fade in on its own (which is
# all MenuBase._fade_out_and_change_scene used to do) still leaves that raw
# pop sitting exposed in the gap between the two fades, which is what read
# as jarring. This covers the whole screen first, changes scenes while
# fully opaque, then reveals — the same "CRT losing signal" static-burst
# language BootSplash and MainMenu's New Game flow already use, just
# centralized here so every menu transition gets it for free.

const BURST_IN_TIME := 0.22
const BURST_HOLD_TIME := 0.12
const BURST_OUT_TIME := 0.28

@onready var _black: ColorRect = $Black
@onready var _static_rect: ColorRect = $StaticRect

var _busy := false

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	_black.modulate.a = 0.0
	_static_rect.visible = false

func change_scene(path: String) -> void:
	if _busy:
		return
	_busy = true

	_static_rect.visible = true
	var mat: ShaderMaterial = _static_rect.material

	var burst_in := create_tween()
	burst_in.tween_property(_black, "modulate:a", 1.0, BURST_IN_TIME)
	burst_in.parallel().tween_method(
		func(v): mat.set_shader_parameter("noise_amount", v), 0.0, 0.5, BURST_IN_TIME
	)
	burst_in.parallel().tween_method(
		func(v): mat.set_shader_parameter("flicker_amount", v), 0.0, 0.6, BURST_IN_TIME
	)
	await burst_in.finished
	await get_tree().create_timer(BURST_HOLD_TIME).timeout

	get_tree().change_scene_to_file(path)
	# Give the new scene a couple of frames to finish _ready() (and its own
	# MenuBase fade-in, if it has one) while still fully hidden behind us.
	await get_tree().process_frame
	await get_tree().process_frame

	var burst_out := create_tween()
	burst_out.tween_property(_black, "modulate:a", 0.0, BURST_OUT_TIME)
	burst_out.parallel().tween_method(
		func(v): mat.set_shader_parameter("noise_amount", v), 0.5, 0.0, BURST_OUT_TIME
	)
	burst_out.parallel().tween_method(
		func(v): mat.set_shader_parameter("flicker_amount", v), 0.6, 0.0, BURST_OUT_TIME
	)
	await burst_out.finished

	_static_rect.visible = false
	_busy = false
