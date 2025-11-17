extends Control

# Reusable settings controls, embedded both by SettingsMenu.tscn (reached
# from the main menu) and PauseMenu.tscn (reached mid-game) so there's one
# implementation instead of two copies drifting apart. This panel doesn't
# decide where "Back" goes — it just emits back_pressed and lets whichever
# parent embedded it handle navigation.

signal back_pressed

const CLICK_SOUND: AudioStream = preload("res://Sound/UI/Menu_Select_00.ogg")

@onready var volume_slider: HSlider = $VBoxContainer/VolumeRow/VolumeSlider
@onready var steady_cam_check: CheckBox = $VBoxContainer/SteadyCamRow/SteadyCamCheck
@onready var fullscreen_check: CheckBox = $VBoxContainer/FullscreenRow/FullscreenCheck
@onready var resolution_option: OptionButton = $VBoxContainer/ResolutionRow/ResolutionOption
@onready var vsync_check: CheckBox = $VBoxContainer/VsyncRow/VsyncCheck
@onready var fps_option: OptionButton = $VBoxContainer/FpsRow/FpsOption
@onready var back_button: Button = $VBoxContainer/BackButton

var _click_player: AudioStreamPlayer

func _ready() -> void:
	var settings = get_node("/root/GameSettings")

	volume_slider.value = settings.MasterVolumeLinear
	steady_cam_check.button_pressed = settings.SteadyCamEnabled
	fullscreen_check.button_pressed = settings.FullscreenEnabled
	vsync_check.button_pressed = settings.VsyncEnabled

	_populate_resolution_options(settings)
	_populate_fps_options(settings)
	resolution_option.disabled = settings.FullscreenEnabled

	_click_player = AudioStreamPlayer.new()
	_click_player.stream = CLICK_SOUND
	_click_player.process_mode = Node.PROCESS_MODE_ALWAYS
	add_child(_click_player)

	volume_slider.value_changed.connect(func(value): settings.SetMasterVolume(value))
	steady_cam_check.toggled.connect(func(enabled): settings.SetSteadyCam(enabled))
	fullscreen_check.toggled.connect(func(enabled):
		settings.SetFullscreen(enabled)
		resolution_option.disabled = enabled
	)
	vsync_check.toggled.connect(func(enabled): settings.SetVsync(enabled))
	resolution_option.item_selected.connect(func(index):
		settings.SetResolution(settings.AvailableResolutions[index])
	)
	fps_option.item_selected.connect(func(index):
		settings.SetMaxFps(settings.AvailableFpsCaps[index])
	)
	back_button.pressed.connect(func():
		_click_player.play()
		back_pressed.emit()
	)

func _populate_resolution_options(settings) -> void:
	resolution_option.clear()
	var current_index := 0
	for i in settings.AvailableResolutions.size():
		var res: Vector2i = settings.AvailableResolutions[i]
		resolution_option.add_item("%d x %d" % [res.x, res.y])
		if res == settings.WindowResolution:
			current_index = i
	resolution_option.select(current_index)

func _populate_fps_options(settings) -> void:
	fps_option.clear()
	var current_index := 0
	for i in settings.AvailableFpsCaps.size():
		var fps: int = settings.AvailableFpsCaps[i]
		fps_option.add_item("UNLIMITED" if fps == 0 else "%d FPS" % fps)
		if fps == settings.MaxFps:
			current_index = i
	fps_option.select(current_index)
