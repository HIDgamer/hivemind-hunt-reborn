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
var _first_menu_button: Button = null

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
	# Keyboard/gamepad nav reuses the same hover glow so a focused button
	# reads identically to a moused-over one — otherwise arrow-key nav would
	# move focus invisibly with no feedback.
	button.focus_mode = Control.FOCUS_ALL
	button.connect("focus_entered", Callable(self, "_on_button_hover").bind(button))
	button.connect("focus_exited", Callable(self, "_on_button_exit").bind(button))

	# First button registered gets focus automatically once the menu is on
	# screen, so ui_up/ui_down/ui_accept work immediately without requiring
	# a mouse click first.
	if _first_menu_button == null:
		_first_menu_button = button
		call_deferred("_grab_initial_focus")

func _grab_initial_focus() -> void:
	if _first_menu_button != null and is_instance_valid(_first_menu_button) and not _first_menu_button.disabled:
		_first_menu_button.grab_focus()

# Escape/Back-gamepad-button acts as a shortcut for whatever this screen's
# own Back button already does — subclasses need nothing extra beyond the
# _on_back_pressed() method most of them already define for their Back
# button's own callback. call() (not a direct call) since MenuBase itself
# doesn't declare that method — only some subclasses do, checked at runtime
# via has_method. Screens with no "back" concept (MainMenu) or their own
# distinct Escape behavior (BootSplash, which fully overrides this instead
# of calling super) are unaffected.
func _unhandled_input(event: InputEvent) -> void:
	if event.is_action_pressed("ui_cancel") and has_method("_on_back_pressed"):
		call("_on_back_pressed")
		get_viewport().set_input_as_handled()

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
	# SceneTransition (an autoload CanvasLayer) covers the screen, changes
	# the scene while fully opaque, then reveals — see its own doc comment
	# for why that replaced this screen fading itself out and just hoping
	# the next scene's own fade-in covered the gap.
	get_node("/root/SceneTransition").change_scene(scene_path)
