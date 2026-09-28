extends Control

# Task-station puzzle body (see TaskPuzzleUI.gd). Lights Out on a 4x4 grid
# of toggle buttons: clicking a cell flips it and its orthogonal neighbors
# (Godot's own toggle_mode already flips the clicked button itself before
# `pressed` fires, so only the neighbors need flipping here). Solve = every
# cell lit. Scrambled by starting fully lit and applying a handful of random
# moves — since a Lights Out move is its own inverse, this guarantees the
# puzzle is always solvable (replaying the same moves solves it).

signal solved

const GRID_SIZE := 4
const SCRAMBLE_MOVES := 6

const CLICK_SOUND := preload("res://Sound/fx/spark.ogg")

var _buttons: Array = []

@onready var _audio: AudioStreamPlayer = $AudioStreamPlayer

func _ready() -> void:
	var grid := get_node("Grid")
	for r in GRID_SIZE:
		for c in GRID_SIZE:
			var button: Button = grid.get_child(r * GRID_SIZE + c)
			_buttons.append(button)
			button.button_pressed = true
			button.pivot_offset = button.size * 0.5
			var row := r
			var col := c
			button.pressed.connect(func(): _on_clicked(row, col))

	for i in SCRAMBLE_MOVES:
		var r := randi() % GRID_SIZE
		var c := randi() % GRID_SIZE
		_flip_cell(r, c)
		_flip_neighbors(r, c)

func _on_clicked(r: int, c: int) -> void:
	_audio.pitch_scale = randf_range(0.9, 1.15)
	_audio.stream = CLICK_SOUND
	_audio.play()
	_punch(_buttons[r * GRID_SIZE + c])
	_flip_neighbors(r, c)
	_check_solved()

func _flip_cell(r: int, c: int) -> void:
	var button: Button = _buttons[r * GRID_SIZE + c]
	button.button_pressed = not button.button_pressed

func _flip_neighbors(r: int, c: int) -> void:
	_maybe_flip(r - 1, c)
	_maybe_flip(r + 1, c)
	_maybe_flip(r, c - 1)
	_maybe_flip(r, c + 1)

func _maybe_flip(r: int, c: int) -> void:
	if r < 0 or r >= GRID_SIZE or c < 0 or c >= GRID_SIZE:
		return
	_flip_cell(r, c)
	_punch(_buttons[r * GRID_SIZE + c])

func _punch(button: Button) -> void:
	var tween := create_tween()
	tween.tween_property(button, "scale", Vector2(1.15, 1.15), 0.06) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)
	tween.tween_property(button, "scale", Vector2.ONE, 0.12) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)

func _check_solved() -> void:
	for button in _buttons:
		if not button.button_pressed:
			return
	solved.emit()
