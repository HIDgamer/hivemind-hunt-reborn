extends Control

# Small "channel ident" monitor above the main menu's button column — shows
# the game's name on its screen and randomly cuts to a brief burst of TV
# static. Reuses the same CRT shader (and the same tween-the-noise-up trick
# MainMenu.gd's own "losing signal" transition already uses for starting a
# new game) rather than needing dedicated static art.

@onready var static_rect: ColorRect = $ScreenStatic
@onready var label: Label = $ScreenLabel

const MIN_INTERVAL := 3.0
const MAX_INTERVAL := 8.0
const BURST_DURATION := 0.35

func _ready() -> void:
	_schedule_next_burst()

func _schedule_next_burst() -> void:
	var delay := randf_range(MIN_INTERVAL, MAX_INTERVAL)
	get_tree().create_timer(delay).timeout.connect(_play_static_burst)

func _play_static_burst() -> void:
	var mat: ShaderMaterial = static_rect.material

	if mat:
		var up := create_tween()
		up.tween_method(func(v): mat.set_shader_parameter("noise_amount", v), 0.0, 0.9, 0.05)
		up.parallel().tween_method(func(v): mat.set_shader_parameter("flicker_amount", v), 0.0, 0.85, 0.05)

	label.visible = false
	await get_tree().create_timer(BURST_DURATION).timeout

	if mat:
		var down := create_tween()
		down.tween_method(func(v): mat.set_shader_parameter("noise_amount", v), 0.9, 0.0, 0.15)
		down.parallel().tween_method(func(v): mat.set_shader_parameter("flicker_amount", v), 0.85, 0.0, 0.15)
	label.visible = true

	_schedule_next_burst()
