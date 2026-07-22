extends MenuBase

# First thing the player sees on launch — a neon title card that powers on
# like a sign catching, then idles the same way MainMenu.gd's RebornLabel
# already does (mostly steady, occasional tube-flicker), so the fixture reads
# as continuous across the cut into the menu rather than two different
# effects. Waits for any key/click — no auto-advance, this is a title card
# meant to be looked at, not a loading screen.

const NEXT_SCENE := "res://Scenes/UI/MainMenu.tscn"
const LIT := Color(1, 1, 1, 1)
const OFF := Color(0.05, 0.05, 0.05, 1.0)

@onready var title_label: Label = $VBoxContainer/TitleLabel
@onready var reborn_label: Label = $VBoxContainer/RebornLabel
@onready var skip_hint: Label = $SkipHint
@onready var loading_label: Label = $LoadingLabel
@onready var crt_scanline_rect: ColorRect = $CRTOverlay/ScanlineRect
@onready var start_audio: AudioStreamPlayer = $StartAudio
@onready var loading_audio: AudioStreamPlayer = $LoadingAudio
@onready var done_audio: AudioStreamPlayer = $DoneAudio

var _advancing := false
var _ambient_active := false

func _ready() -> void:
	# Not calling super._ready() — MenuBase's uniform fade-in doesn't fit a
	# sign that's meant to catch unevenly rather than dissolve in smoothly.
	modulate = Color(1, 1, 1, 1)
	title_label.modulate = OFF
	reborn_label.modulate = OFF
	skip_hint.modulate = Color(1, 1, 1, 0)

	_run_ignition()

func _run_ignition() -> void:
	start_audio.play()
	var catches := [0.05, 0.09, 0.04, 0.18, 0.05, 0.4]
	for i in catches.size():
		title_label.modulate = LIT if i % 2 == 0 else OFF
		await get_tree().create_timer(catches[i]).timeout
	title_label.modulate = LIT

	await get_tree().create_timer(0.35).timeout

	var reborn_catches := [0.04, 0.07, 0.04, 0.25]
	for i in reborn_catches.size():
		reborn_label.modulate = LIT if i % 2 == 0 else OFF
		await get_tree().create_timer(reborn_catches[i]).timeout
	reborn_label.modulate = LIT

	var hint_tween := create_tween()
	hint_tween.tween_property(skip_hint, "modulate", LIT, 0.6)
	hint_tween.tween_callback(_pulse_skip_hint)

	_ambient_active = true
	_run_reborn_cycle()

# Full-brightness text was still easy to lose against the parallax/CRT noise
# sitting still — a slow breathing pulse (not a flicker, that's the sign's
# language, not the prompt's) is what actually catches the eye.
func _pulse_skip_hint() -> void:
	if _advancing:
		return
	var pulse := create_tween()
	pulse.tween_property(skip_hint, "modulate:a", 0.55, 0.9) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	pulse.tween_property(skip_hint, "modulate:a", 1.0, 0.9) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	pulse.tween_callback(_pulse_skip_hint)

# Same surge/flicker shape as MainMenu.gd's _run_reborn_cycle/
# _maybe_flicker_reborn — duplicated rather than shared since it's a couple
# small tween chains, not worth extracting a helper for one other caller.
func _run_reborn_cycle() -> void:
	if not _ambient_active:
		return
	var surge := create_tween()
	surge.tween_property(reborn_label, "modulate", Color(1.12, 0.96, 0.94, 1.0), 1.7) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	surge.tween_property(reborn_label, "modulate", Color(0.9, 0.82, 0.8, 0.96), 1.7) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	surge.tween_callback(_maybe_flicker_reborn)

func _maybe_flicker_reborn() -> void:
	if not _ambient_active:
		return
	if randf() > 0.35:
		_run_reborn_cycle()
		return

	var flick := create_tween()
	flick.tween_property(reborn_label, "modulate", Color(0.25, 0.16, 0.15, 0.7), 0.05)
	flick.tween_property(reborn_label, "modulate", Color(1.5, 1.2, 1.15, 1.0), 0.06)
	flick.tween_property(reborn_label, "modulate", Color(0.4, 0.26, 0.24, 0.8), 0.04)
	flick.tween_property(reborn_label, "modulate", LIT, 0.12)
	flick.tween_callback(_run_reborn_cycle)

func _unhandled_input(event: InputEvent) -> void:
	if _advancing:
		return
	if (event is InputEventKey and event.pressed and not event.echo) \
		or (event is InputEventMouseButton and event.pressed):
		_advance()

func _advance() -> void:
	if _advancing:
		return
	_advancing = true
	_ambient_active = false

	var power_down := create_tween()
	power_down.tween_property(skip_hint, "modulate:a", 0.0, 0.15)
	power_down.parallel().tween_property(title_label, "modulate", OFF, 0.15)
	power_down.parallel().tween_property(reborn_label, "modulate", OFF, 0.15)
	await power_down.finished

	await _play_crt_loading_burst()
	get_tree().change_scene_to_file(NEXT_SCENE)

# A fake "losing signal" cut instead of a plain fade to black — the CRT
# overlay's own scanline noise/flicker spikes hard for a moment, like an old
# monitor struggling to hold a picture through a scene change, while a terse
# LOADING readout catches and holds. Reuses CRTOverlay's existing shader
# rather than building a separate effect for a one-time transition.
func _play_crt_loading_burst() -> void:
	loading_audio.play()
	var mat: ShaderMaterial = crt_scanline_rect.material
	const BASE_NOISE := 0.05
	const BASE_FLICKER := 0.05
	const BURST_NOISE := 0.4
	const BURST_FLICKER := 0.55

	if mat:
		var noise_up := create_tween()
		noise_up.tween_method(
			func(v): mat.set_shader_parameter("noise_amount", v), BASE_NOISE, BURST_NOISE, 0.25
		)
		noise_up.parallel().tween_method(
			func(v): mat.set_shader_parameter("flicker_amount", v), BASE_FLICKER, BURST_FLICKER, 0.25
		)

	var loading_catches := [0.05, 0.08, 0.04, 0.3]
	for i in loading_catches.size():
		loading_label.modulate = Color(1, 1, 1, 1) if i % 2 == 0 else Color(1, 1, 1, 0)
		await get_tree().create_timer(loading_catches[i]).timeout
	loading_label.modulate = Color(1, 1, 1, 1)

	await get_tree().create_timer(0.5).timeout

	done_audio.play()
	var settle := create_tween()
	settle.tween_property(loading_label, "modulate:a", 0.0, 0.2)
	if mat:
		settle.parallel().tween_method(
			func(v): mat.set_shader_parameter("noise_amount", v), BURST_NOISE, BASE_NOISE, 0.2
		)
		settle.parallel().tween_method(
			func(v): mat.set_shader_parameter("flicker_amount", v), BURST_FLICKER, BASE_FLICKER, 0.2
		)
	await settle.finished
