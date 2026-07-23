extends CanvasLayer

# Autoload — a single full-screen ColorRect sitting above absolutely
# everything else (layer is set higher than PauseMenu's 100 in the .tscn),
# so the brightness setting affects gameplay, HUD, CRT overlays, and menus
# alike instead of needing to be threaded through each one individually.

@onready var _rect: ColorRect = $ColorRect

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	var settings = get_node("/root/GameSettings")
	_apply(settings)
	settings.SettingsChanged.connect(func(): _apply(settings))

func _apply(settings) -> void:
	_rect.material.set_shader_parameter("brightness", settings.BrightnessLevel)
