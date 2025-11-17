extends MenuBase

@onready var singleplayer_button = $VBoxContainer/SingleplayerButton
@onready var multiplayer_button = $VBoxContainer/MultiplayerButton
@onready var settings_button = $VBoxContainer/SettingsButton
@onready var quit_button = $VBoxContainer/QuitButton
@onready var reborn_label = $VBoxContainer/RebornLabel

func _ready() -> void:
	super._ready()
	_connect_menu_button(singleplayer_button, Callable(self, "_on_singleplayer_pressed"))
	_connect_menu_button(multiplayer_button, Callable(self, "_on_multiplayer_pressed"))
	_connect_menu_button(settings_button, Callable(self, "_on_settings_pressed"))
	_connect_menu_button(quit_button, Callable(self, "_on_quit_pressed"))
	_start_reborn_pulse()

# The REBORN sign behaves like the ship's other dying fixtures: mostly
# steady with a barely-there energy surge, and every so often a quick
# tube-flicker — not a constant deep breathing loop.
func _start_reborn_pulse() -> void:
	if reborn_label == null:
		return
	_run_reborn_cycle()

func _run_reborn_cycle() -> void:
	var surge := create_tween()
	surge.tween_property(reborn_label, "modulate", Color(1.12, 0.96, 0.94, 1.0), 1.7) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	surge.tween_property(reborn_label, "modulate", Color(0.9, 0.82, 0.8, 0.96), 1.7) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	surge.tween_callback(_maybe_flicker_reborn)

func _maybe_flicker_reborn() -> void:
	if randf() > 0.35:
		_run_reborn_cycle()
		return

	# A failing neon tube: dark blip, overbright snap, dark blip, settle.
	var flick := create_tween()
	flick.tween_property(reborn_label, "modulate", Color(0.25, 0.16, 0.15, 0.7), 0.05)
	flick.tween_property(reborn_label, "modulate", Color(1.5, 1.2, 1.15, 1.0), 0.06)
	flick.tween_property(reborn_label, "modulate", Color(0.4, 0.26, 0.24, 0.8), 0.04)
	flick.tween_property(reborn_label, "modulate", Color(1, 1, 1, 1), 0.12)
	flick.tween_callback(_run_reborn_cycle)

func _on_singleplayer_pressed():
	# A prior multiplayer session (hosted or joined, then backed out to this
	# menu) leaves a live MultiplayerPeer behind — without tearing it down
	# here, the tutorial level would see IsNetworked still true and try to
	# spawn networked players instead of just using the plain single Sam.
	get_node("/root/NetworkManager").Disconnect()
	_fade_out_and_change_scene("uid://dpo7v6ksd0n07") # Level_00_Tutorial.tscn

func _on_multiplayer_pressed():
	_fade_out_and_change_scene("res://Scenes/UI/Lobby.tscn")

func _on_settings_pressed():
	_fade_out_and_change_scene("uid://ddehdkr23m4qr")

func _on_quit_pressed():
	get_tree().quit()
