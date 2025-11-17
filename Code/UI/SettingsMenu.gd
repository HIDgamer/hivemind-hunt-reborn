extends Control

@onready var back_button = $VBoxContainer/BackButton

var fade_tween: Tween
var button_tweens: Dictionary = {}

func _ready():
    # Connect button signals
    back_button.connect("pressed", Callable(self, "_on_back_pressed"))

    # Connect hover signals for animations
    back_button.connect("mouse_entered", Callable(self, "_on_button_hover").bind(back_button))
    back_button.connect("mouse_exited", Callable(self, "_on_button_exit").bind(back_button))

    # Fade in animation using tween
    modulate = Color(1, 1, 1, 0)
    fade_tween = create_tween()
    fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 1), 0.5)

func _on_back_pressed():
    _fade_out_and_change_scene("res://Scenes/UI/MainMenu.tscn")

func _on_button_hover(button: Button):
    # Simple scale up effect - use per-button tweens
    if button_tweens.has(button) and button_tweens[button] and button_tweens[button].is_valid():
        button_tweens[button].kill()
    button_tweens[button] = create_tween()
    button_tweens[button].tween_property(button, "scale", Vector2(1.1, 1.1), 0.2).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

func _on_button_exit(button: Button):
    # Simple scale back effect - use per-button tweens
    if button_tweens.has(button) and button_tweens[button] and button_tweens[button].is_valid():
        button_tweens[button].kill()

    button_tweens[button] = create_tween()
    button_tweens[button].tween_property(button, "scale", Vector2(1, 1), 0.2).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)

func _fade_out_and_change_scene(scene_path: String):
    # Fade out and change scene using tween
    if fade_tween and fade_tween.is_valid():
        fade_tween.kill()
    fade_tween = create_tween()
    fade_tween.tween_property(self, "modulate", Color(1, 1, 1, 0), 0.5)
    await fade_tween.finished
    get_tree().change_scene_to_file(scene_path)