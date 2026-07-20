extends MenuBase

# First thing the player sees on launch — a neon title card that powers on
# like a sign catching, then idles the same way MainMenu.gd's RebornLabel
# already does (mostly steady, occasional tube-flicker), so the fixture reads
# as continuous across the cut into the menu rather than two different
# effects. Skippable via any key/click, otherwise auto-advances.

const NEXT_SCENE := "res://Scenes/UI/MainMenu.tscn"
const AUTO_ADVANCE_SECONDS := 5.0
const LIT := Color(1, 1, 1, 1)
const OFF := Color(0.05, 0.05, 0.05, 1.0)

@onready var title_label: Label = $VBoxContainer/TitleLabel
@onready var reborn_label: Label = $VBoxContainer/RebornLabel
@onready var skip_hint: Label = $SkipHint

var _advancing := false
var _ambient_active := false

func _ready() -> void:
	# Not calling super._ready() — MenuBase's uniform fade-in doesn't fit a
	# sign that's meant to catch unevenly rather than dissolve in smoothly.
	modulate = Color(1, 1, 1, 1)
	title_label.modulate = OFF
	reborn_label.modulate = OFF
	skip_hint.modulate = Color(1, 1, 1, 0)

	_run_ignition()
	get_tree().create_timer(AUTO_ADVANCE_SECONDS).timeout.connect(_advance)

func _run_ignition() -> void:
	var catches := [0.05, 0.09, 0.04, 0.18, 0.05, 0.4]
	for i in catches.size():
		title_label.modulate = LIT if i % 2 == 0 else OFF
		await get_tree().create_timer(catches[i]).timeout
	title_label.modulate = LIT

	await get_tree().create_timer(0.35).timeout

	var reborn_catches := [0.04, 0.07, 0.04, 0.25]
	for i in reborn_catches.size():
		reborn_label.modulate = LIT if i % 2 == 0 else OFF
		await get_tree().create_timer(reborn_catches[i]).timeout
	reborn_label.modulate = LIT

	var hint_tween := create_tween()
	hint_tween.tween_property(skip_hint, "modulate", Color(0.6, 0.6, 0.6, 0.8), 0.6)

	_ambient_active = true
	_run_reborn_cycle()

# Same surge/flicker shape as MainMenu.gd's _run_reborn_cycle/
# _maybe_flicker_reborn — duplicated rather than shared since it's a couple
# small tween chains, not worth extracting a helper for one other caller.
func _run_reborn_cycle() -> void:
	if not _ambient_active:
		return
	var surge := create_tween()
	surge.tween_property(reborn_label, "modulate", Color(1.12, 0.96, 0.94, 1.0), 1.7) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	surge.tween_property(reborn_label, "modulate", Color(0.9, 0.82, 0.8, 0.96), 1.7) \
		.set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN_OUT)
	surge.tween_callback(_maybe_flicker_reborn)

func _maybe_flicker_reborn() -> void:
	if not _ambient_active:
		return
	if randf() > 0.35:
		_run_reborn_cycle()
		return

	var flick := create_tween()
	flick.tween_property(reborn_label, "modulate", Color(0.25, 0.16, 0.15, 0.7), 0.05)
	flick.tween_property(reborn_label, "modulate", Color(1.5, 1.2, 1.15, 1.0), 0.06)
	flick.tween_property(reborn_label, "modulate", Color(0.4, 0.26, 0.24, 0.8), 0.04)
	flick.tween_property(reborn_label, "modulate", LIT, 0.12)
	flick.tween_callback(_run_reborn_cycle)

func _unhandled_input(event: InputEvent) -> void:
	if _advancing:
		return
	if (event is InputEventKey and event.pressed and not event.echo) \
		or (event is InputEventMouseButton and event.pressed):
		_advance()

func _advance() -> void:
	if _advancing:
		return
	_advancing = true
	_ambient_active = false
	_fade_out_and_change_scene(NEXT_SCENE)
