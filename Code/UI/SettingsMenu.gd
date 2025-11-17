extends MenuBase

@onready var settings_panel = $VBoxContainer/SettingsPanelInstance

func _ready() -> void:
	super._ready()
	settings_panel.back_pressed.connect(_on_back_pressed)

func _on_back_pressed():
	_fade_out_and_change_scene("uid://cnw6i1v1qt1rh")
