extends Control

# Sits as the last (top) child of a menu's Control tree. Purely cosmetic —
# ignores mouse input entirely so it never blocks the buttons underneath.

@onready var rec_dot: ColorRect = $HUDText/RecRow/RecDot
@onready var feed_label: Label = $HUDText/FeedLabel

var _elapsed: float = 0.0

func _process(delta: float) -> void:
	_elapsed += delta
	rec_dot.visible = fmod(_elapsed, 1.0) < 0.6

	var total_seconds := int(_elapsed)
	@warning_ignore("integer_division")
	var hours := total_seconds / 3600
	@warning_ignore("integer_division")
	var minutes := (total_seconds / 60) % 60
	var seconds := total_seconds % 60
	feed_label.text = "SEC-CAM 04 // FEED ACTIVE // %02d:%02d:%02d" % [hours, minutes, seconds]
