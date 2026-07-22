extends CanvasLayer

# Autoload — always present in the tree so any level can be paused without
# each scene needing its own copy. process_mode = ALWAYS lets this node
# keep processing input while get_tree().paused freezes everything else.

const CLICK_SOUND: AudioStream = preload("res://Sound/UI/Menu_Select_00.ogg")

@onready var background: ColorRect = $Background
@onready var menu_root: Control = $MenuRoot
@onready var button_box: VBoxContainer = $MenuRoot/VBoxContainer
@onready var resume_button: Button = $MenuRoot/VBoxContainer/ResumeButton
@onready var save_button: Button = $MenuRoot/VBoxContainer/SaveButton
@onready var load_button: Button = $MenuRoot/VBoxContainer/LoadButton
@onready var settings_button: Button = $MenuRoot/VBoxContainer/SettingsButton
@onready var quit_button: Button = $MenuRoot/VBoxContainer/QuitButton
@onready var settings_panel: Control = $MenuRoot/SettingsPanelInstance
@onready var save_load_menu = $MenuRoot/SaveLoadMenuInstance
@onready var ambient_player: AudioStreamPlayer = $AudioStreamPlayer

var _is_open: bool = false
var _showing_settings: bool = false
var _showing_saveload: bool = false
var _click_player: AudioStreamPlayer

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	visible = false

	_click_player = AudioStreamPlayer.new()
	_click_player.stream = CLICK_SOUND
	_click_player.process_mode = Node.PROCESS_MODE_ALWAYS
	add_child(_click_player)

	resume_button.pressed.connect(_on_resume_pressed)
	save_button.pressed.connect(_on_save_pressed)
	load_button.pressed.connect(_on_load_pressed)
	settings_button.pressed.connect(_on_settings_pressed)
	quit_button.pressed.connect(_on_quit_pressed)
	settings_panel.back_pressed.connect(_on_settings_back)
	save_load_menu.back_pressed.connect(_on_saveload_back)
	save_load_menu.slot_selected.connect(_on_save_slot_loaded)

func _unhandled_input(event: InputEvent) -> void:
	if not event.is_action_pressed("Pause"):
		return

	if _is_open and _showing_settings:
		_on_settings_back()
	elif _is_open and _showing_saveload:
		_on_saveload_back()
	elif _is_open:
		close_pause()
	else:
		_try_open_pause()

	get_viewport().set_input_as_handled()

func _try_open_pause() -> void:
	# Only makes sense mid-gameplay — pressing Escape on the main menu
	# itself shouldn't try to pause it.
	if get_tree().get_nodes_in_group("Player").is_empty():
		return
	open_pause()

func open_pause() -> void:
	_is_open = true
	_showing_settings = false
	_showing_saveload = false
	button_box.visible = true
	settings_panel.visible = false
	save_load_menu.visible = false
	visible = true
	get_tree().paused = true
	ambient_player.play()
	resume_button.grab_focus()

	# Save/Load only make sense against a single, locally-owned checkpoint
	# trail — a live multiplayer session has no per-slot state to write, and
	# loading one would only reposition this one peer, desyncing everyone else.
	var networked: bool = get_node("/root/NetworkManager").IsNetworked
	save_button.disabled = networked
	load_button.disabled = networked or not get_node("/root/SaveManager").HasAnySave()

func close_pause() -> void:
	_is_open = false
	visible = false
	get_tree().paused = false
	ambient_player.stop()

func _on_resume_pressed() -> void:
	_click_player.play()
	close_pause()

func _on_settings_pressed() -> void:
	_click_player.play()
	_showing_settings = true
	button_box.visible = false
	settings_panel.visible = true
	settings_panel.grab_initial_focus()

func _on_settings_back() -> void:
	_showing_settings = false
	settings_panel.visible = false
	button_box.visible = true
	settings_button.grab_focus()

func _on_save_pressed() -> void:
	_click_player.play()
	_showing_saveload = true
	save_load_menu.mode = save_load_menu.Mode.SAVE
	save_load_menu.refresh()
	button_box.visible = false
	save_load_menu.visible = true
	save_load_menu.grab_initial_focus()

func _on_load_pressed() -> void:
	_click_player.play()
	_showing_saveload = true
	save_load_menu.mode = save_load_menu.Mode.LOAD
	save_load_menu.refresh()
	button_box.visible = false
	save_load_menu.visible = true
	save_load_menu.grab_initial_focus()

func _on_saveload_back() -> void:
	_showing_saveload = false
	save_load_menu.visible = false
	button_box.visible = true
	# Load may have just become newly available (a save was made in Save
	# mode, or an autosave landed while this screen happened to be open).
	load_button.disabled = get_node("/root/NetworkManager").IsNetworked \
		or not get_node("/root/SaveManager").HasAnySave()
	save_button.grab_focus()

# A save picked from the pause menu's Load screen — unlike New Game's CRT
# announcement (a fresh run's whole point is the ceremony), resuming mid-play
# just wants to get back into the game, so this cuts straight to the scene.
# SaveManager.LoadSlot already queued the respawn position on CheckpointManager.
func _on_save_slot_loaded(scene_path: String) -> void:
	close_pause()
	get_tree().change_scene_to_file(scene_path)

func _on_quit_pressed() -> void:
	_click_player.play()
	close_pause()
	# Leaving a live multiplayer session behind here would make the main
	# menu's own Singleplayer button misbehave (the level would still see
	# NetworkManager reporting connected and try to spawn networked players).
	get_node("/root/NetworkManager").Disconnect()
	get_tree().change_scene_to_file("uid://cnw6i1v1qt1rh")
