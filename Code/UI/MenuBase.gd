class_name MenuBase
extends Control

# Shared fade-in/fade-out and button hover/press feel for every menu screen —
# previously copy-pasted identically between MainMenu.gd and SettingsMenu.gd.

const CLICK_SOUND: AudioStream = preload("res://Sound/UI/Menu_Select_00.ogg")
const HOVER_COLOR := Color(1.15, 1.4, 1.2, 1.0)
const FLASH_COLOR := Color(2.2, 2.6, 2.2, 1.0)
const NORMAL_COLOR := Color(1, 1, 1, 1)

var fade_tween: Tween
var button_tweens: Dictionary = {}
var _click_player: AudioStreamPlayer

func _ready() -> void:
	modulate = Color(1, 1, 1, 0)
	fade_tween = create_tween()
	fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 1), 0.5)

	_click_player = AudioStreamPlayer.new()
	_click_player.stream = CLICK_SOUND
	add_child(_click_player)

# Wires a button's press callback plus the hover/press feedback in one call —
# subclasses call this per button after super._ready().
func _connect_menu_button(button: Button, callback: Callable) -> void:
	if button == null:
		return
	# Scaling from the corner (Control's default pivot) reads lopsided —
	# center it so the hover/press punch grows evenly in both directions.
	button.pivot_offset = button.size * 0.5
	button.resized.connect(func(): button.pivot_offset = button.size * 0.5)

	button.connect("pressed", Callable(self, "_on_menu_button_pressed").bind(button, callback))
	button.connect("mouse_entered", Callable(self, "_on_button_hover").bind(button))
	button.connect("mouse_exited", Callable(self, "_on_button_exit").bind(button))

func _on_menu_button_pressed(button: Button, callback: Callable) -> void:
	if _click_player:
		_click_player.play()
	_flicker_press(button)
	callback.call()

# A quick bright spike settling into a highlighted glow, plus a small
# corner-centered scale punch — reads as a terminal signal flaring to life
# rather than a soft, bouncy mobile-game scale-up.
func _on_button_hover(button: Button) -> void:
	if button.disabled:
		return
	_kill_button_tween(button)
	button.modulate = FLASH_COLOR
	var tween := create_tween()
	button_tweens[button] = tween
	tween.tween_property(button, "modulate", HOVER_COLOR, 0.18).set_trans(Tween.TRANS_EXPO).set_ease(Tween.EASE_OUT)
	tween.parallel().tween_property(button, "scale", Vector2(1.05, 1.05), 0.15).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

func _on_button_exit(button: Button) -> void:
	_kill_button_tween(button)
	var tween := create_tween()
	button_tweens[button] = tween
	tween.tween_property(button, "modulate", NORMAL_COLOR, 0.15)
	tween.parallel().tween_property(button, "scale", Vector2(1, 1), 0.15).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

# A short dark-then-overbright flicker on activation, like a switch briefly
# browning out the feed, settling back to the hover glow (mouse is still
# over the button when this fires).
func _flicker_press(button: Button) -> void:
	_kill_button_tween(button)
	var tween := create_tween()
	button_tweens[button] = tween
	tween.tween_property(button, "modulate", Color(0.35, 0.45, 0.4, 1.0), 0.03)
	tween.tween_property(button, "modulate", Color(2.4, 2.6, 2.4, 1.0), 0.04)
	tween.tween_property(button, "modulate", HOVER_COLOR, 0.14)

func _kill_button_tween(button: Button) -> void:
	if button_tweens.has(button) and button_tweens[button] and button_tweens[button].is_valid():
		button_tweens[button].kill()

func _fade_out_and_change_scene(scene_path: String) -> void:
	if fade_tween and fade_tween.is_valid():
		fade_tween.kill()
	fade_tween = create_tween()
	fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 0), 0.5)
	await fade_tween.finished
	get_tree().change_scene_to_file(scene_path)
