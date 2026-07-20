extends Control

# Reusable settings controls, embedded both by SettingsMenu.tscn (reached
# from the main menu) and PauseMenu.tscn (reached mid-game) so there's one
# implementation instead of two copies drifting apart. This panel doesn't
# decide where "Back" goes — it just emits back_pressed and lets whichever
# parent embedded it handle navigation.

signal back_pressed

const CLICK_SOUND: AudioStream = preload("res://Sound/UI/Menu_Select_00.ogg")

@onready var volume_slider: HSlider = $ScrollContainer/VBoxContainer/VolumeRow/VolumeSlider
@onready var music_volume_slider: HSlider = (
	$ScrollContainer/VBoxContainer/MusicVolumeRow/MusicVolumeSlider
)
@onready var sfx_volume_slider: HSlider = (
	$ScrollContainer/VBoxContainer/SFXVolumeRow/SFXVolumeSlider
)
@onready var voice_volume_slider: HSlider = (
	$ScrollContainer/VBoxContainer/VoiceVolumeRow/VoiceVolumeSlider
)
@onready var idle_barks_check: CheckBox = (
	$ScrollContainer/VBoxContainer/IdleBarksRow/IdleBarksCheck
)
@onready var voice_chat_disabled_check: CheckBox = (
	$ScrollContainer/VBoxContainer/VoiceChatDisabledRow/VoiceChatDisabledCheck
)
@onready var mic_device_option: OptionButton = (
	$ScrollContainer/VBoxContainer/MicDeviceRow/MicDeviceOption
)
@onready var mic_gain_slider: HSlider = $ScrollContainer/VBoxContainer/MicGainRow/MicGainSlider
@onready var mic_test_button: Button = $ScrollContainer/VBoxContainer/MicTestRow/MicTestButton
@onready var mic_test_bar: ProgressBar = $ScrollContainer/VBoxContainer/MicTestRow/MicTestBar
@onready var crt_theme_option: OptionButton = (
	$ScrollContainer/VBoxContainer/CrtThemeRow/CrtThemeOption
)
@onready var steady_cam_check: CheckBox = (
	$ScrollContainer/VBoxContainer/SteadyCamRow/SteadyCamCheck
)
@onready var fullscreen_check: CheckBox = (
	$ScrollContainer/VBoxContainer/FullscreenRow/FullscreenCheck
)
@onready var resolution_option: OptionButton = (
	$ScrollContainer/VBoxContainer/ResolutionRow/ResolutionOption
)
@onready var vsync_check: CheckBox = $ScrollContainer/VBoxContainer/VsyncRow/VsyncCheck
@onready var fps_option: OptionButton = $ScrollContainer/VBoxContainer/FpsRow/FpsOption
@onready var back_button: Button = $ScrollContainer/VBoxContainer/BackButton

var _click_player: AudioStreamPlayer
var _voice_chat_manager
var _mic_test_held := false

func _ready() -> void:
	var settings = get_node("/root/GameSettings")
	_voice_chat_manager = get_node("/root/VoiceChatManager")

	volume_slider.value = settings.MasterVolumeLinear
	music_volume_slider.value = settings.MusicVolumeLinear
	sfx_volume_slider.value = settings.SFXVolumeLinear
	voice_volume_slider.value = settings.VoiceVolumeLinear
	mic_gain_slider.value = settings.MicGainLinear
	idle_barks_check.button_pressed = settings.IdleBarksMuted
	voice_chat_disabled_check.button_pressed = settings.VoiceChatDisabled
	steady_cam_check.button_pressed = settings.SteadyCamEnabled
	fullscreen_check.button_pressed = settings.FullscreenEnabled
	vsync_check.button_pressed = settings.VsyncEnabled

	_populate_resolution_options(settings)
	_populate_fps_options(settings)
	_populate_crt_theme_options(settings)
	_populate_mic_device_options(settings)
	resolution_option.disabled = settings.FullscreenEnabled

	_click_player = AudioStreamPlayer.new()
	_click_player.stream = CLICK_SOUND
	_click_player.process_mode = Node.PROCESS_MODE_ALWAYS
	add_child(_click_player)

	volume_slider.value_changed.connect(func(value): settings.SetMasterVolume(value))
	music_volume_slider.value_changed.connect(func(value): settings.SetMusicVolume(value))
	sfx_volume_slider.value_changed.connect(func(value): settings.SetSFXVolume(value))
	voice_volume_slider.value_changed.connect(func(value): settings.SetVoiceVolume(value))
	mic_gain_slider.value_changed.connect(func(value): settings.SetMicGain(value))
	idle_barks_check.toggled.connect(func(enabled): settings.SetIdleBarksMuted(enabled))
	voice_chat_disabled_check.toggled.connect(
		func(disabled): settings.SetVoiceChatDisabled(disabled)
	)
	crt_theme_option.item_selected.connect(func(index): settings.SetCrtTheme(index))
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
	mic_device_option.item_selected.connect(func(index):
		settings.SetMicDevice(mic_device_option.get_item_metadata(index))
	)
	mic_test_button.button_down.connect(func():
		_mic_test_held = true
		_voice_chat_manager.SetTestModeActive(true)
	)
	mic_test_button.button_up.connect(func():
		_stop_mic_test()
	)
	# PauseMenu toggles this panel's .visible rather than removing it from
	# the tree, so _exit_tree alone wouldn't catch e.g. Escape closing the
	# pause menu mid-hold.
	visibility_changed.connect(func():
		if not visible:
			_stop_mic_test()
	)
	back_button.pressed.connect(func():
		_click_player.play()
		_stop_mic_test()
		back_pressed.emit()
	)

func _process(_delta: float) -> void:
	if _mic_test_held:
		mic_test_bar.value = _voice_chat_manager.CurrentInputLevel

# Safety net for anything that ends the test besides releasing the button
# itself (Back button, Escape, this panel getting hidden/freed mid-hold) —
# without this the mic could keep sampling silently in the background.
func _stop_mic_test() -> void:
	if not _mic_test_held:
		return
	_mic_test_held = false
	_voice_chat_manager.SetTestModeActive(false)
	mic_test_bar.value = 0.0

func _exit_tree() -> void:
	_stop_mic_test()

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

func _populate_mic_device_options(settings) -> void:
	mic_device_option.clear()
	# "Default" (the OS's own default recording device) always comes first,
	# regardless of what AudioServer's raw device list order happens to be —
	# it's the sane default for anyone with a single mic, and always valid
	# even on a machine with none plugged in.
	mic_device_option.add_item("DEFAULT")
	mic_device_option.set_item_metadata(0, "Default")
	var devices := AudioServer.get_input_device_list()
	var current_index := 0
	for i in devices.size():
		var device_name: String = devices[i]
		if device_name == "Default":
			continue
		mic_device_option.add_item(device_name.to_upper())
		mic_device_option.set_item_metadata(mic_device_option.item_count - 1, device_name)
		if device_name == settings.MicDeviceName:
			current_index = mic_device_option.item_count - 1
	mic_device_option.select(current_index)

func _populate_crt_theme_options(settings) -> void:
	crt_theme_option.clear()
	for i in settings.CrtThemeNames.size():
		crt_theme_option.add_item(settings.CrtThemeNames[i])
	crt_theme_option.select(settings.CrtThemeIndex)
