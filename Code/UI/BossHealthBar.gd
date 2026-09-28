extends CanvasLayer

# Autoload (see project.godot). A single shared boss health-bar widget any
# BossArenaTrigger can show/hide — same "one shared shell, autoload-owned"
# pattern as TaskPuzzleUI.gd/DialogueUI. show_for binds to whichever
# HealthComponent is passed in (a plain Node reference — GDScript reads its
# MaxHealth/CurrentHealth/HealthChanged/Died dynamically, same idiom
# EnemyBase.gd already uses for its own _health field); hide_bar releases
# that binding so a second boss elsewhere doesn't fight over a stale
# connection.

@onready var panel: Control = $Panel
@onready var name_label: Label = $Panel/NameLabel
@onready var fill: ProgressBar = $Panel/Fill

var _health: Node = null

func _ready() -> void:
	panel.visible = false

func show_for(health: Node, boss_name: String) -> void:
	_release()
	_health = health
	if _health == null or not is_instance_valid(_health):
		return

	name_label.text = boss_name
	fill.max_value = _health.MaxHealth
	fill.value = _health.CurrentHealth

	if _health.has_signal("HealthChanged"):
		_health.HealthChanged.connect(_on_health_changed)
	if _health.has_signal("Died"):
		_health.Died.connect(_on_died)

	panel.visible = true
	panel.modulate = Color(1, 1, 1, 0)
	var tween := create_tween()
	tween.tween_property(panel, "modulate:a", 1.0, 0.3)

func hide_bar() -> void:
	_release()
	panel.visible = false

func _on_health_changed(current: int, max_health: int) -> void:
	fill.max_value = max_health
	var tween := create_tween()
	tween.tween_property(fill, "value", float(current), 0.25)

func _on_died() -> void:
	hide_bar()

func _release() -> void:
	if _health != null and is_instance_valid(_health):
		if _health.has_signal("HealthChanged") and _health.HealthChanged.is_connected(_on_health_changed):
			_health.HealthChanged.disconnect(_on_health_changed)
		if _health.has_signal("Died") and _health.Died.is_connected(_on_died):
			_health.Died.disconnect(_on_died)
	_health = null
