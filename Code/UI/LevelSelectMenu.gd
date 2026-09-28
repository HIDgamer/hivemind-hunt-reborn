extends Control

# Embedded in MainMenu (same shape as LoadGameMenu.gd — a Control MainMenu
# toggles visible/hidden rather than a scene change of its own). Lists every
# level in LevelProgress's authored order, greying out anything not yet
# unlocked. Deliberately reads LevelProgress live each time refresh() runs
# rather than caching — MainMenu calls refresh() every time this screen is
# shown, so a level completed earlier this session is reflected immediately.

signal back_pressed
signal level_chosen(scene_path: String)

@onready var _list: VBoxContainer = $Background/VBoxContainer/LevelList
@onready var _back_button: Button = $Background/VBoxContainer/BackButton

func _ready() -> void:
	_back_button.pressed.connect(func(): back_pressed.emit())

func refresh() -> void:
	for child in _list.get_children():
		child.queue_free()

	var progress = get_node("/root/LevelProgress")
	for scene_path in progress.GetLevelOrder():
		var unlocked: bool = progress.IsUnlocked(scene_path)
		var completed: bool = progress.IsCompleted(scene_path)

		var label: String = progress.GetDisplayName(scene_path)
		if completed:
			label += "   [DONE]"
		elif not unlocked:
			label += "   [LOCKED]"

		var button := Button.new()
		button.text = label
		button.custom_minimum_size = Vector2(0, 44)
		button.disabled = not unlocked
		if unlocked:
			button.pressed.connect(func(): level_chosen.emit(scene_path))
		_list.add_child(button)
