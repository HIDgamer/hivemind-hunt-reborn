extends CanvasLayer

# Autoload singleton (see project.godot). Any NpcDialogueTrigger calls
# DialogueUI.start_dialogue(lines) with an array of
# {speaker, portrait, text} dictionaries — this owns the box, the
# typewriter reveal, and advancing/closing.

signal dialogue_started
signal dialogue_ended

@onready var panel: Panel = $Panel
@onready var portrait_rect: TextureRect = $Panel/PortraitFrame/Portrait
@onready var name_label: Label = $Panel/NameLabel
@onready var text_label: RichTextLabel = $Panel/TextLabel
@onready var continue_hint: Label = $Panel/ContinueHint

const CHARS_PER_SECOND := 42.0

var _lines: Array = []
var _line_index := 0
var _reveal_progress := 0.0
var _line_finished := false
var _local_player: Node = null

func _ready() -> void:
	panel.visible = false
	set_process_unhandled_input(false)

func _process(delta: float) -> void:
	if _lines.is_empty():
		return

	if not _line_finished:
		_reveal_progress += delta * CHARS_PER_SECOND
		var full_text: String = _lines[_line_index].get("text", "")
		var shown := int(_reveal_progress)
		if shown >= full_text.length():
			shown = full_text.length()
			_line_finished = true
			continue_hint.visible = true
		text_label.visible_characters = shown

func _unhandled_input(event: InputEvent) -> void:
	if _lines.is_empty():
		return

	# Advancing only ever listened on "Interact" — "Jump" was also accepted,
	# but Jump is bound to three physical keys (W/Space/Up) AND is read every
	# physics tick by Sam's own jump-buffer. The moment a Jump press closed
	# the LAST line, UiInputCaptured cleared mid-frame and that same
	# still-"just pressed" state got picked up as a real jump input a tick
	# later — Sam would hop, and depending on exactly when the next line's
	# reveal/finished state landed relative to that, advancing could look
	# like it silently ate presses. One unambiguous key removes all of that
	# cross-talk. Escape is now the guaranteed bail-out regardless of state.
	if event.is_action_pressed("Interact"):
		get_viewport().set_input_as_handled()
		_advance()
	elif event.is_action_pressed("ui_cancel"):
		get_viewport().set_input_as_handled()
		_end_dialogue()

# lines: Array[Dictionary] — each {speaker: String, portrait: Texture2D, text: String}
func start_dialogue(lines: Array, local_player: Node = null) -> void:
	if lines.is_empty():
		return

	_lines = lines
	_line_index = 0
	_local_player = local_player
	if _local_player != null and "UiInputCaptured" in _local_player:
		_local_player.UiInputCaptured = true

	panel.visible = true
	set_process_unhandled_input(true)
	_show_current_line()
	dialogue_started.emit()

func _show_current_line() -> void:
	var line: Dictionary = _lines[_line_index]
	name_label.text = line.get("speaker", "")
	var portrait = line.get("portrait", null)
	portrait_rect.texture = portrait
	portrait_rect.get_parent().visible = portrait != null

	text_label.text = line.get("text", "")
	text_label.visible_characters = 0
	_reveal_progress = 0.0
	_line_finished = false
	continue_hint.visible = false

func _advance() -> void:
	if not _line_finished:
		# First press skips the typewriter instead of advancing.
		var full_text: String = _lines[_line_index].get("text", "")
		text_label.visible_characters = full_text.length()
		_line_finished = true
		continue_hint.visible = true
		return

	_line_index += 1
	if _line_index >= _lines.size():
		# The closing press and the press an NpcDialogueTrigger reads to
		# open a NEW conversation are the exact same physical keystroke.
		# call_deferred alone isn't a strong enough guarantee here — Godot
		# flushes that queue at more than one point per frame, and it can
		# run BEFORE other nodes' _Process this same frame rather than
		# strictly after, which would let is_active() go false in time for
		# the trigger to still catch it and reopen — matching exactly what
		# kept happening. Awaiting the next process frame is unambiguous:
		# is_active() is guaranteed to stay true for the ENTIRE remainder of
		# this frame (nothing else runs until the next frame begins), and by
		# the time _end_dialogue actually executes, the original key-down is
		# no longer "just pressed" anywhere, so nothing can mistake it for a
		# fresh press.
		await get_tree().process_frame
		_end_dialogue()
		return

	_show_current_line()

func _end_dialogue() -> void:
	_lines = []
	panel.visible = false
	set_process_unhandled_input(false)
	if _local_player != null and "UiInputCaptured" in _local_player:
		_local_player.UiInputCaptured = false
	_local_player = null
	dialogue_ended.emit()

func is_active() -> bool:
	return not _lines.is_empty()
