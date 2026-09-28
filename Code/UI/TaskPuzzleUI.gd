extends CanvasLayer

# Autoload singleton (see project.godot). Any TaskStationBase calls
# TaskPuzzleUI.open(puzzle_scene, title, on_solved, local_player) to show one
# of the Among-Us-style task minigames. One shared shell (frame, title,
# cancel button, Sam input-lock) for all of them — each puzzle body is just
# a Control instanced into PuzzleContainer that emits "solved" when won; the
# shell itself knows nothing puzzle-specific. Same open/close/safety-net
# shape as DialogueBox.gd, including locking UiInputCaptured on the player
# passed in.
#
# Shell-level juice lives entirely here rather than per-puzzle: a scale/fade
# open, the same scale/fade close on both cancel and solve, and a distinct
# success flash+chime before the close plays on solve — every puzzle body
# gets this for free just by emitting `solved`, no per-puzzle wiring needed.

@onready var panel: Panel = $Panel
@onready var title_label: Label = $Panel/TitleLabel
@onready var puzzle_container: Control = $Panel/PuzzleContainer
@onready var cancel_button: Button = $Panel/CancelButton
@onready var _audio: AudioStreamPlayer = $AudioStreamPlayer

const OPEN_SOUND := preload("res://Sound/UI/CRT-Start.ogg")
const CANCEL_SOUND := preload("res://Sound/UI/tick.ogg")
const SOLVED_SOUND := preload("res://Sound/fx/Jingle_Win_00.ogg")

const OPEN_DURATION := 0.18
const CLOSE_DURATION := 0.14
const SOLVED_FLASH_UP := 0.12
const SOLVED_FLASH_DOWN := 0.38
const CLOSED_SCALE := Vector2(0.85, 0.85)

var _puzzle_body: Control = null
var _on_solved: Callable
var _local_player: Node = null

func _ready() -> void:
	panel.visible = false
	panel.pivot_offset = panel.size * 0.5
	set_process_unhandled_input(false)
	cancel_button.pressed.connect(_on_cancel_pressed)

func _unhandled_input(event: InputEvent) -> void:
	if event.is_action_pressed("ui_cancel"):
		get_viewport().set_input_as_handled()
		_on_cancel_pressed()

func is_active() -> bool:
	return _puzzle_body != null

# puzzle_scene's root must be a Control that declares `signal solved`.
# config is handed to the puzzle body's own `configure(Dictionary)` if it
# has one (e.g. TerminalBypassPuzzle's sequence length) — puzzles that
# don't need external setup (PipePatch, PowerReroute) simply don't
# implement configure, and this is a no-op for them.
func open(
	puzzle_scene: PackedScene,
	title: String,
	on_solved: Callable,
	local_player: Node = null,
	config: Dictionary = {}
) -> void:
	if _puzzle_body != null:
		return

	_on_solved = on_solved
	_local_player = local_player
	if _local_player != null and "UiInputCaptured" in _local_player:
		_local_player.UiInputCaptured = true

	title_label.text = title
	_puzzle_body = puzzle_scene.instantiate()
	puzzle_container.add_child(_puzzle_body)
	_puzzle_body.solved.connect(_on_puzzle_solved)
	if _puzzle_body.has_method("configure"):
		_puzzle_body.configure(config)

	cancel_button.disabled = false
	panel.visible = true
	panel.modulate = Color(1, 1, 1, 0)
	panel.scale = CLOSED_SCALE
	set_process_unhandled_input(true)

	_audio.pitch_scale = 1.0
	_audio.stream = OPEN_SOUND
	_audio.play()

	var open_tween := create_tween().set_parallel()
	open_tween.tween_property(panel, "modulate:a", 1.0, OPEN_DURATION) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)
	open_tween.tween_property(panel, "scale", Vector2.ONE, OPEN_DURATION) \
		.set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

func _on_cancel_pressed() -> void:
	if _puzzle_body == null:
		return
	_audio.pitch_scale = 1.0
	_audio.stream = CANCEL_SOUND
	_audio.play()
	_close()

func _on_puzzle_solved() -> void:
	if _puzzle_body == null:
		return

	# The actual gameplay effect (door unlock, vent repair, power restore)
	# fires immediately rather than waiting on the shell's own outro — only
	# the popup's own dismissal is what's animated/delayed below.
	var callback := _on_solved
	if callback.is_valid():
		callback.call()

	set_process_unhandled_input(false)
	cancel_button.disabled = true

	_audio.pitch_scale = 1.0
	_audio.stream = SOLVED_SOUND
	_audio.play()

	var flash := create_tween()
	flash.tween_property(panel, "modulate", Color(1.4, 1.9, 1.5, 1.0), SOLVED_FLASH_UP)
	flash.tween_property(panel, "modulate", Color(1, 1, 1, 1), SOLVED_FLASH_DOWN)
	flash.tween_callback(_close)

func _close() -> void:
	if _puzzle_body == null:
		return

	set_process_unhandled_input(false)
	var body := _puzzle_body
	var player := _local_player
	_puzzle_body = null
	_local_player = null

	var close_tween := create_tween().set_parallel()
	close_tween.tween_property(panel, "modulate:a", 0.0, CLOSE_DURATION) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)
	close_tween.tween_property(panel, "scale", CLOSED_SCALE, CLOSE_DURATION) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)
	close_tween.chain().tween_callback(func():
		panel.visible = false
		panel.scale = Vector2.ONE
		panel.modulate = Color(1, 1, 1, 1)
		body.queue_free()
		if player != null and "UiInputCaptured" in player:
			player.UiInputCaptured = false
	)
