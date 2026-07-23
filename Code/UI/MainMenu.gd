extends MenuBase

const TUTORIAL_LEVEL := "uid://dpo7v6ksd0n07" # Level_00_Tutorial.tscn

@onready var tutorial_button = $VBoxContainer/TutorialButton
@onready var new_game_button = $VBoxContainer/NewGameButton
@onready var load_button = $VBoxContainer/LoadButton
@onready var multiplayer_button = $VBoxContainer/MultiplayerButton
@onready var settings_button = $VBoxContainer/SettingsButton
@onready var quit_button = $VBoxContainer/QuitButton
@onready var reborn_label = $VBoxContainer/RebornLabel
@onready var load_game_menu = $LoadGameMenuInstance
@onready var start_game_label: Label = $StartGameLabel
@onready var start_game_audio: AudioStreamPlayer = $StartGameAudio
@onready var crt_scanline_rect: ColorRect = $CRTOverlay/ScanlineRect

func _ready() -> void:
	super._ready()
	_connect_menu_button(tutorial_button, Callable(self, "_on_tutorial_pressed"))
	# No real level 1 exists yet — only the tutorial is playable right now, so
	# New Game stays greyed out (like Load with no saves) until there's an
	# actual first level for it to start.
	new_game_button.disabled = true
	_connect_menu_button(new_game_button, Callable(self, "_on_new_game_pressed"))
	_connect_menu_button(multiplayer_button, Callable(self, "_on_multiplayer_pressed"))
	_connect_menu_button(settings_button, Callable(self, "_on_settings_pressed"))
	_connect_menu_button(quit_button, Callable(self, "_on_quit_pressed"))

	load_button.disabled = not get_node("/root/SaveManager").HasAnySave()
	_connect_menu_button(load_button, Callable(self, "_on_load_pressed"))

	load_game_menu.back_pressed.connect(_on_load_game_back)
	load_game_menu.slot_selected.connect(_on_save_slot_selected)

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

func _on_tutorial_pressed():
	# A prior multiplayer session (hosted or joined, then backed out to this
	# menu) leaves a live MultiplayerPeer behind — without tearing it down
	# here, the tutorial level would see IsNetworked still true and try to
	# spawn networked players instead of just using the plain single Sam.
	get_node("/root/NetworkManager").Disconnect()
	# Same reasoning as New Game: browsing the Load screen (or a save loaded
	# earlier this session) can leave a pending checkpoint respawn queued on
	# CheckpointManager — Tutorial is a standalone practice run, never a
	# resume, and must never inherit it.
	get_node("/root/CheckpointManager").ClearPendingRespawn()
	_fade_out_and_change_scene(TUTORIAL_LEVEL)

func _on_new_game_pressed():
	get_node("/root/NetworkManager").Disconnect()
	# A fresh run starts with a clean slate — any checkpoint left over from a
	# previous session (or from browsing the Load screen) must not leak in
	# and reposition Sam somewhere she hasn't actually reached yet this run.
	get_node("/root/CheckpointManager").ClearPendingRespawn()
	_start_new_game_and_change_scene(TUTORIAL_LEVEL)

func _on_load_pressed():
	load_game_menu.refresh()
	button_box_visible(false)
	load_game_menu.visible = true
	load_game_menu.grab_initial_focus()

func _on_load_game_back():
	load_game_menu.visible = false
	button_box_visible(true)
	# Deleting the last save from inside the Load screen must gray Load back
	# out immediately — this only ever ran once, in _ready(), so it stayed
	# stale (still enabled) after returning from a Load screen that emptied
	# out without the whole MainMenu scene reloading.
	load_button.disabled = not get_node("/root/SaveManager").HasAnySave()
	load_button.grab_focus()

func _on_save_slot_selected(scene_path: String):
	get_node("/root/NetworkManager").Disconnect()
	_start_new_game_and_change_scene(scene_path, false)

func button_box_visible(should_show: bool) -> void:
	$VBoxContainer.visible = should_show

# CRT "losing signal" burst reused from BootSplash's boot-to-menu transition,
# so a new run announces itself with the same visual language as the game's
# very first cut — with start-level.ogg guaranteed to finish playing before
# the level actually loads (silence-then-cut-mid-line would undersell the
# moment far more than a couple extra seconds on a black screen would).
func _start_new_game_and_change_scene(scene_path: String, play_intro_audio := true) -> void:
	if fade_tween and fade_tween.is_valid():
		fade_tween.kill()
	fade_tween = create_tween()
	fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 0), 0.5)
	await fade_tween.finished

	if play_intro_audio:
		start_game_audio.play()

	var mat: ShaderMaterial = crt_scanline_rect.material
	const BASE_NOISE := 0.05
	const BASE_FLICKER := 0.05
	const BURST_NOISE := 0.4
	const BURST_FLICKER := 0.55

	if mat:
		var noise_up := create_tween()
		noise_up.tween_method(
			func(v): mat.set_shader_parameter("noise_amount", v), BASE_NOISE, BURST_NOISE, 0.3
		)
		noise_up.parallel().tween_method(
			func(v): mat.set_shader_parameter("flicker_amount", v), BASE_FLICKER, BURST_FLICKER, 0.3
		)

	var label_catches := [0.05, 0.08, 0.04, 0.3]
	for i in label_catches.size():
		start_game_label.modulate = Color(1, 1, 1, 1) if i % 2 == 0 else Color(1, 1, 1, 0)
		await get_tree().create_timer(label_catches[i]).timeout
	start_game_label.modulate = Color(1, 1, 1, 1)

	if play_intro_audio:
		await start_game_audio.finished
	else:
		await get_tree().create_timer(0.6).timeout

	get_tree().change_scene_to_file(scene_path)

func _on_multiplayer_pressed():
	_fade_out_and_change_scene("res://Scenes/UI/Lobby.tscn")

func _on_settings_pressed():
	_fade_out_and_change_scene("uid://ddehdkr23m4qr")

func _on_quit_pressed():
	get_tree().quit()
