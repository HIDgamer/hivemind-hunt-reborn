extends HBoxContainer

# One row of the Gameplay tab's control-rebinding list: a label plus two
# capture buttons (keyboard/mouse slot, gamepad slot). Clicking a button
# enters "press a key/button" mode; the next matching input event replaces
# just that slot on the action, leaving the other slot's binding untouched,
# then persists both slots together via GameSettings.SaveKeybindPair so a
# restart can reconstruct the action unambiguously.

const CAPTURE_TEXT := "PRESS..."

@export var action_name: String = ""
@export var display_name: String = ""

@onready var label: Label = $Label
@onready var key_button: Button = $KeyButton
@onready var joy_button: Button = $JoyButton

var _capturing_key := false
var _capturing_joy := false

func _ready() -> void:
	label.text = display_name
	_refresh_labels()
	key_button.pressed.connect(func(): _start_capture(true))
	joy_button.pressed.connect(func(): _start_capture(false))

func _start_capture(is_key: bool) -> void:
	if _capturing_key or _capturing_joy:
		return
	if is_key:
		_capturing_key = true
		key_button.text = CAPTURE_TEXT
	else:
		_capturing_joy = true
		joy_button.text = CAPTURE_TEXT

func _unhandled_input(event: InputEvent) -> void:
	if not (_capturing_key or _capturing_joy):
		return

	if event.is_action_pressed("ui_cancel"):
		_cancel_capture()
		get_viewport().set_input_as_handled()
		return

	if _capturing_key:
		if event is InputEventKey and event.pressed and not event.echo:
			_apply_key_event(event)
			get_viewport().set_input_as_handled()
		elif event is InputEventMouseButton and event.pressed:
			_apply_key_event(event)
			get_viewport().set_input_as_handled()
	elif _capturing_joy:
		if event is InputEventJoypadButton and event.pressed:
			_apply_joy_event(event)
			get_viewport().set_input_as_handled()
		elif event is InputEventJoypadMotion and absf(event.axis_value) > 0.5:
			_apply_joy_event(event)
			get_viewport().set_input_as_handled()

func _apply_key_event(event: InputEvent) -> void:
	var settings = get_node("/root/GameSettings")
	var joy_evt := _current_joy_event()
	InputMap.action_erase_events(action_name)
	InputMap.action_add_event(action_name, event.duplicate())
	if joy_evt:
		InputMap.action_add_event(action_name, joy_evt)
	settings.SaveKeybindPair(action_name, event.duplicate(), joy_evt)
	_capturing_key = false
	_refresh_labels()

func _apply_joy_event(event: InputEvent) -> void:
	var settings = get_node("/root/GameSettings")
	var key_evt := _current_key_event()
	InputMap.action_erase_events(action_name)
	if key_evt:
		InputMap.action_add_event(action_name, key_evt)
	InputMap.action_add_event(action_name, event.duplicate())
	settings.SaveKeybindPair(action_name, key_evt, event.duplicate())
	_capturing_joy = false
	_refresh_labels()

func _cancel_capture() -> void:
	_capturing_key = false
	_capturing_joy = false
	_refresh_labels()

func _current_key_event() -> InputEvent:
	for e in InputMap.action_get_events(action_name):
		if e is InputEventKey or e is InputEventMouseButton:
			return e
	return null

func _current_joy_event() -> InputEvent:
	for e in InputMap.action_get_events(action_name):
		if e is InputEventJoypadButton or e is InputEventJoypadMotion:
			return e
	return null

func _refresh_labels() -> void:
	key_button.text = _event_display(_current_key_event())
	joy_button.text = _event_display(_current_joy_event())

func _event_display(event: InputEvent) -> String:
	if event == null:
		return "UNBOUND"
	if event is InputEventKey:
		return OS.get_keycode_string(event.physical_keycode).to_upper()
	if event is InputEventMouseButton:
		return "MOUSE %d" % event.button_index
	if event is InputEventJoypadButton:
		return "BTN %d" % event.button_index
	if event is InputEventJoypadMotion:
		return "AXIS %d%s" % [event.axis, "+" if event.axis_value >= 0 else "-"]
	return "?"
