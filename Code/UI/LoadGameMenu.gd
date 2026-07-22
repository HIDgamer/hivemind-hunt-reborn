extends Control

# Embedded in both MainMenu (load-only) and PauseMenu (load + manual save) the
# same way SettingsPanel is embedded — the parent toggles .visible and reacts
# to these signals rather than this menu owning any scene transition itself.
# Caller sets `mode` before calling refresh()/grab_initial_focus().

signal back_pressed
signal slot_selected(scene_path: String) # load mode: a slot was loaded
signal save_completed # save mode: a slot was written (parent may want to re-check HasAnySave)

enum Mode { LOAD, SAVE }

const CLICK_SOUND: AudioStream = preload("res://Sound/UI/Menu_Select_00.ogg")

var mode: int = Mode.LOAD

@onready var title_label: Label = $VBoxContainer/Title
@onready var back_button: Button = $VBoxContainer/BackButton
@onready var confirm_dialog: ConfirmationDialog = $ConfirmDialog
@onready var _rows: Array = [
	$VBoxContainer/SlotList/Slot1Row,
	$VBoxContainer/SlotList/Slot2Row,
	$VBoxContainer/SlotList/Slot3Row,
	$VBoxContainer/SlotList/Slot4Row,
	$VBoxContainer/SlotList/Slot5Row,
]

var _click_player: AudioStreamPlayer
# What the confirm dialog is currently waiting on ("save" or "delete") plus
# the slot it applies to — a single shared dialog, so this is set right
# before .popup_centered() and read back in _on_confirm_dialog_confirmed.
var _pending_action := ""
var _pending_slot := 0

func _ready() -> void:
	_click_player = AudioStreamPlayer.new()
	_click_player.stream = CLICK_SOUND
	add_child(_click_player)

	back_button.pressed.connect(func():
		_click_player.play()
		back_pressed.emit()
	)
	confirm_dialog.confirmed.connect(_on_confirm_dialog_confirmed)

	for row in _rows:
		var action_button: Button = row.get_node("ActionButton")
		var delete_button: Button = row.get_node("DeleteButton")
		action_button.pressed.connect(_on_action_pressed.bind(row))
		delete_button.pressed.connect(_on_delete_pressed.bind(row))

# Called by the parent right before showing this panel, so it always
# reflects whatever's actually on disk and the current mode's button labels.
func refresh() -> void:
	title_label.text = "SAVE GAME" if mode == Mode.SAVE else "LOAD GAME"
	var save_manager = get_node("/root/SaveManager")
	var slots = save_manager.GetSlots()
	for i in _rows.size():
		_populate_row(_rows[i], slots[i])

func grab_initial_focus() -> void:
	for row in _rows:
		var action_button: Button = row.get_node("ActionButton")
		if not action_button.disabled:
			action_button.grab_focus()
			return
	back_button.grab_focus()

func _populate_row(row: Node, slot_info: Dictionary) -> void:
	var name_label: Label = row.get_node("Info/NameLabel")
	var time_label: Label = row.get_node("Info/TimeLabel")
	var thumbnail: TextureRect = row.get_node("Thumbnail")
	var action_button: Button = row.get_node("ActionButton")
	var delete_button: Button = row.get_node("DeleteButton")

	action_button.text = "SAVE" if mode == Mode.SAVE else "LOAD"
	action_button.set_meta("slot", slot_info["Slot"])
	delete_button.set_meta("slot", slot_info["Slot"])

	if not slot_info["Occupied"]:
		name_label.text = "EMPTY"
		time_label.text = ""
		thumbnail.texture = null
		# In save mode an empty slot is exactly what you'd want to save into.
		action_button.disabled = (mode == Mode.LOAD)
		delete_button.disabled = true
		return

	name_label.text = slot_info["DisplayName"]
	time_label.text = slot_info["Timestamp"]
	action_button.disabled = false
	delete_button.disabled = false

	var image := Image.new()
	if image.load(slot_info["ThumbnailPath"]) == OK:
		thumbnail.texture = ImageTexture.create_from_image(image)
	else:
		thumbnail.texture = null

func _on_action_pressed(row: Node) -> void:
	var action_button: Button = row.get_node("ActionButton")
	var slot: int = action_button.get_meta("slot")
	var name_label: Label = row.get_node("Info/NameLabel")
	_click_player.play()

	if mode == Mode.LOAD:
		var save_manager = get_node("/root/SaveManager")
		var scene_path: String = save_manager.LoadSlot(slot)
		if scene_path != "":
			slot_selected.emit(scene_path)
		return

	# Save mode: an occupied slot needs a confirm (overwriting real progress),
	# an empty one doesn't (nothing to lose).
	if name_label.text == "EMPTY":
		_do_manual_save(slot)
	else:
		_pending_action = "save"
		_pending_slot = slot
		confirm_dialog.dialog_text = "Overwrite \"%s\"?" % name_label.text
		confirm_dialog.popup_centered()

func _on_delete_pressed(row: Node) -> void:
	var delete_button: Button = row.get_node("DeleteButton")
	var slot: int = delete_button.get_meta("slot")
	var name_label: Label = row.get_node("Info/NameLabel")
	_click_player.play()

	_pending_action = "delete"
	_pending_slot = slot
	confirm_dialog.dialog_text = "Delete \"%s\"? This can't be undone." % name_label.text
	confirm_dialog.popup_centered()

func _on_confirm_dialog_confirmed() -> void:
	if _pending_action == "save":
		_do_manual_save(_pending_slot)
	elif _pending_action == "delete":
		get_node("/root/SaveManager").DeleteSlot(_pending_slot)
		refresh()
	_pending_action = ""

func _do_manual_save(slot: int) -> void:
	var save_manager = get_node("/root/SaveManager")
	var scene_path := get_tree().current_scene.scene_file_path

	var player_pos := Vector2.ZERO
	for node in get_tree().get_nodes_in_group("Player"):
		if not node.get("IsNetworked"):
			player_pos = node.global_position
			break

	save_manager.ManualSave(slot, scene_path, player_pos)
	refresh()
	save_completed.emit()
