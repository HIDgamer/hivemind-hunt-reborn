extends Control

# Task-station puzzle body (see TaskPuzzleUI.gd for the shared shell
# contract: root Control, emits `solved` once). 4 leak points, each with a
# fill bar that bounces back and forth (a triangle wave) while its HOLD
# button is held down; releasing checks whether the bar was inside that
# row's randomized target zone. Land all 4 to solve. Missing resets that
# row's bar to empty rather than ending the puzzle — no fail state, just
# "try that one again."

signal solved

const LEAK_COUNT := 4
const OSCILLATE_SPEED := 1.3 # bounce cycles per second
const TARGET_WIDTH := 0.16 # fraction of the bar counted as "sealed"

const HISS_SOUND := preload("res://Sound/fx/steam hisses - 2.ogg")
const SEAL_SOUND := preload("res://Sound/Footsteps/cogs.ogg")
const MISS_SOUND := preload("res://Sound/UI/tick.ogg")

var _fills: Array = []
var _zones: Array = []
var _buttons: Array = []
var _bar_areas: Array = []
var _held: Array = []
var _sealed: Array = []
var _targets: Array = []
var _time := 0.0

@onready var _hiss: AudioStreamPlayer = $HissAudio
@onready var _sfx: AudioStreamPlayer = $SfxAudio

func _ready() -> void:
	for i in LEAK_COUNT:
		var row := get_node("Rows/LeakRow%d" % (i + 1))
		var bar_area: Control = row.get_node("BarArea")
		var fill: ProgressBar = bar_area.get_node("Fill")
		var zone: ColorRect = bar_area.get_node("TargetZone")
		var button: Button = row.get_node("HoldButton")

		_fills.append(fill)
		_zones.append(zone)
		_buttons.append(button)
		_bar_areas.append(bar_area)
		_held.append(false)
		_sealed.append(false)

		var target := randf_range(0.2, 0.8)
		_targets.append(target)
		_position_zone(zone, target)

		var index := i
		button.button_down.connect(func(): _on_hold_start(index))
		button.button_up.connect(func(): _on_hold_end(index))

# Anchors, not pixel offsets — the zone's rect resolves against BarArea's
# own size every frame regardless of when layout finishes, so this doesn't
# need to wait for a layout pass to know BarArea's actual pixel width.
func _position_zone(zone: ColorRect, target: float) -> void:
	zone.anchor_left = clampf(target - TARGET_WIDTH * 0.5, 0.0, 1.0)
	zone.anchor_right = clampf(target + TARGET_WIDTH * 0.5, 0.0, 1.0)
	zone.anchor_top = 0.0
	zone.anchor_bottom = 1.0
	zone.offset_left = 0.0
	zone.offset_right = 0.0
	zone.offset_top = 0.0
	zone.offset_bottom = 0.0

func _on_hold_start(index: int) -> void:
	if _sealed[index]:
		return
	_held[index] = true
	_hiss.pitch_scale = randf_range(0.95, 1.05)
	_hiss.stream = HISS_SOUND
	_hiss.play()

func _on_hold_end(index: int) -> void:
	if _sealed[index]:
		return
	_held[index] = false
	_hiss.stop()
	var value: float = _fills[index].value / 100.0
	if absf(value - _targets[index]) <= TARGET_WIDTH * 0.5:
		_seal(index)
	else:
		_fills[index].value = 0.0
		_sfx.pitch_scale = 0.8
		_sfx.stream = MISS_SOUND
		_sfx.play()
		_shake(_bar_areas[index])

func _seal(index: int) -> void:
	_sealed[index] = true
	_fills[index].value = _targets[index] * 100.0
	_zones[index].color = Color(0.3, 1.0, 0.5, 0.7)
	_buttons[index].disabled = true
	_buttons[index].text = "SEALED"

	_sfx.pitch_scale = 1.0
	_sfx.stream = SEAL_SOUND
	_sfx.play()
	_pulse(_bar_areas[index])

	for s in _sealed:
		if not s:
			return
	solved.emit()

func _pulse(control: Control) -> void:
	control.pivot_offset = control.size * 0.5
	var tween := create_tween()
	tween.tween_property(control, "scale", Vector2(1.06, 1.35), 0.08) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)
	tween.tween_property(control, "scale", Vector2.ONE, 0.15) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)

func _shake(control: Control) -> void:
	var base_pos: Vector2 = control.position
	var tween := create_tween()
	tween.tween_property(control, "position", base_pos + Vector2(5, 0), 0.035)
	tween.tween_property(control, "position", base_pos - Vector2(5, 0), 0.035)
	tween.tween_property(control, "position", base_pos, 0.035)

func _process(delta: float) -> void:
	_time += delta
	for i in LEAK_COUNT:
		if _sealed[i] or not _held[i]:
			continue
		var phase := fmod(_time * OSCILLATE_SPEED, 2.0)
		var wave: float = phase if phase <= 1.0 else 2.0 - phase
		_fills[i].value = wave * 100.0
