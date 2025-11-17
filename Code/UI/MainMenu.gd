extends Control

@onready var singleplayer_button = $VBoxContainer/SingleplayerButton
@onready var multiplayer_button = $VBoxContainer/MultiplayerButton
@onready var settings_button = $VBoxContainer/SettingsButton
@onready var quit_button = $VBoxContainer/QuitButton

var fade_tween: Tween
var button_tweens: Dictionary = {}

func _ready():
	# Connect button signals
	singleplayer_button.connect("pressed", Callable(self, "_on_singleplayer_pressed"))
	multiplayer_button.connect("pressed", Callable(self, "_on_multiplayer_pressed"))
	settings_button.connect("pressed", Callable(self, "_on_settings_pressed"))
	quit_button.connect("pressed", Callable(self, "_on_quit_pressed"))

	# Connect hover signals for animations
	for button in [singleplayer_button, multiplayer_button, settings_button, quit_button]:
		button.connect("mouse_entered", Callable(self, "_on_button_hover").bind(button))
		button.connect("mouse_exited", Callable(self, "_on_button_exit").bind(button))

	# Fade in animation using tween
	modulate = Color(1, 1, 1, 0)
	fade_tween = create_tween()
	fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 1), 0.5)

func _on_singleplayer_pressed():
	# Singleplayer is null for now
	print("Singleplayer pressed - not implemented")

func _on_multiplayer_pressed():
	# Transition to multiplayer menu
	_fade_out_and_change_scene("res://Scenes/UI/MultiplayerUI.tscn")

func _on_settings_pressed():
	# Transition to settings menu
	_fade_out_and_change_scene("res://Scenes/UI/SettingsMenu.tscn")

func _on_quit_pressed():
	get_tree().quit()

func _on_button_hover(button: Button):
	# Simple scale up effect - use per-button tweens
	if button_tweens.has(button) and button_tweens[button] and button_tweens[button].is_valid():
		button_tweens[button].kill()
	button_tweens[button] = create_tween()
	button_tweens[button].tween_property(button, "scale", Vector2(1.1, 1.1), 0.2).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

func _on_button_exit(button: Button):
	# Simple scale back effect - use per-button tweens
	if button_tweens.has(button) and button_tweens[button] and button_tweens[button].is_valid():
		button_tweens[button].kill()
	button_tweens[button] = create_tween()
	button_tweens[button].tween_property(button, "scale", Vector2(1, 1), 0.2).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

func _fade_out_and_change_scene(scene_path: String):
	# Fade out and change scene using tween
	if fade_tween and fade_tween.is_valid():
		fade_tween.kill()
	fade_tween = create_tween()
	fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 0), 0.5)
	await fade_tween.finished
	get_tree().change_scene_to_file(scene_path)
