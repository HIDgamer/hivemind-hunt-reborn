extends Control

# Task-station puzzle body (see TaskPuzzleUI.gd). Simon-Says: a sequence of
# BUTTON_COUNT colored panels flashes, player clicks them back in the same
# order; a wrong click restarts the same sequence from the top (unlimited
# retries, v1 scope) rather than failing the puzzle outright. Sequence
# length comes from `configure({"length": N})`, called by TaskPuzzleUI right
# after this is added to the tree if the station passed a config — falls
# back to DEFAULT_LENGTH if nothing ever calls configure. _ready() only
# wires up node references; nothing plays until configure runs, so a
# station-provided length is never raced by an auto-started default
# sequence.

signal solved

const BUTTON_COUNT := 4
const FLASH_DURATION := 0.35
const GAP_DURATION := 0.15
const DEFAULT_LENGTH := 4

# Each button gets its own flash pitch (a rough 4-note chime) so the
# sequence actually reads as something to listen for, not just a repeated
# blip — the same pitch plays again when the player clicks that button back.
const FLASH_PITCHES := [1.0, 1.18, 1.4, 1.65]
const WRONG_SOUND := preload("res://Sound/UI/boing_x.ogg")
const TONE_SOUND := preload("res://Sound/UI/tick.ogg")

var _buttons: Array = []
var _base_colors: Array = []
var _base_scales: Array = []
var _sequence: Array = []
var _player_index := 0
var _accepting_input := false

@onready var _audio: AudioStreamPlayer = $AudioStreamPlayer

func _ready() -> void:
	for i in BUTTON_COUNT:
		var button: Button = get_node("Grid").get_child(i)
		_buttons.append(button)
		_base_colors.append(button.modulate)
		_base_scales.append(button.scale)
		button.pivot_offset = button.size * 0.5
		var index := i
		button.pressed.connect(func(): _on_pressed(index))

func configure(config: Dictionary) -> void:
	var length: int = config.get("length", DEFAULT_LENGTH)
	_sequence.clear()
	for i in length:
		_sequence.append(randi() % BUTTON_COUNT)
	_player_index = 0
	_play_sequence()

func _play_sequence() -> void:
	_accepting_input = false
	for button in _buttons:
		button.disabled = true

	for step in _sequence:
		await get_tree().create_timer(GAP_DURATION).timeout
		await _flash(step)

	for button in _buttons:
		button.disabled = false
	_accepting_input = true

func _flash(index: int) -> void:
	var button: Button = _buttons[index]
	_play_tone(index)
	button.modulate = Color(1.7, 1.7, 1.7, 1.0)
	_punch(button, index)
	await get_tree().create_timer(FLASH_DURATION).timeout
	button.modulate = _base_colors[index]

func _on_pressed(index: int) -> void:
	if not _accepting_input:
		return

	if index == _sequence[_player_index]:
		_play_tone(index)
		_punch(_buttons[index], index)
		_player_index += 1
		if _player_index >= _sequence.size():
			_accepting_input = false
			solved.emit()
	else:
		_audio.pitch_scale = 1.0
		_audio.stream = WRONG_SOUND
		_audio.play()
		_shake_all()
		_player_index = 0
		_play_sequence()

func _play_tone(index: int) -> void:
	_audio.pitch_scale = FLASH_PITCHES[index % FLASH_PITCHES.size()]
	_audio.stream = TONE_SOUND
	_audio.play()

func _punch(button: Button, index: int) -> void:
	var base_scale: Vector2 = _base_scales[index]
	var tween := create_tween()
	tween.tween_property(button, "scale", base_scale * 1.12, FLASH_DURATION * 0.35) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)
	tween.tween_property(button, "scale", base_scale, FLASH_DURATION * 0.65) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)

func _shake_all() -> void:
	for button in _buttons:
		var base_pos: Vector2 = button.position
		var tween := create_tween()
		tween.tween_property(button, "position", base_pos + Vector2(6, 0), 0.04)
		tween.tween_property(button, "position", base_pos - Vector2(6, 0), 0.04)
		tween.tween_property(button, "position", base_pos + Vector2(3, 0), 0.04)
		tween.tween_property(button, "position", base_pos, 0.04)
