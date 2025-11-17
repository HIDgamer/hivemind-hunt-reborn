extends Camera2D
class_name PlayerCamera

# A focused 2D platformer camera: smooth follow with a deadzone, light
# velocity-based look-ahead, zoom control, trauma-based screen shake, and a
# damage-flash overlay tied to the target's HealthComponent.
#
# Trimmed from a much larger version that also carried an EMP effect, a
# vacuum/oxygen exposure system, room-based camera limits with a blur
# transition, static/vignette shader overlays, and mouse-based look-ahead —
# none of which were reachable from anywhere in the project: no node was
# ever placed in a "room" group, no StaticOverlay/VignetteOverlay/BlurEffect
# child was ever added under this camera, and apply_emp/set_vacuum_exposure/
# focus_on_position/set_static had zero callers anywhere in the codebase.
# If any of those are wanted later, this is the point to rebuild from —
# check git history for the full version.

@export var target_path: NodePath

@export_group("Movement")
@export var camera_speed: float = 3.0  # Lower = smoother but slower
@export var deadzone_size: Vector2 = Vector2(10, 10)
# Disables look-ahead and screen shake for a rock-steady, motion-sickness-friendly camera.
@export var steady_cam: bool = false

@export_group("Zoom")
@export var default_zoom: Vector2 = Vector2(1.0, 1.0)
@export var zoom_speed: float = 3.0
@export var min_zoom: float = 0.5
@export var max_zoom: float = 3.0

@export_group("Look Ahead")
@export var look_ahead_factor: float = 0.1
@export var max_look_ahead: float = 80.0
@export var look_ahead_smoothing: float = 0.15

@export_group("Screen Shake")
@export var trauma_reduction_rate: float = 1.2
@export var max_trauma: float = 1.0
@export var max_shake_offset: Vector2 = Vector2(25, 15)
@export var max_shake_rotation: float = 0.02
@export var noise_shake_speed: float = 20.0
@export var trauma_exponent: float = 2.0  # Higher = more easing (2.0 = quadratic falloff)
@export var damage_shake_trauma: float = 0.15

@export_group("Damage Flash")
@export var damage_flash_duration: float = 0.12
@export var damage_flash_color: Color = Color(1.0, 0.05, 0.02, 0.34)

@onready var noise: FastNoiseLite = FastNoiseLite.new()

var target: Node2D = null
var trauma: float = 0.0
var target_zoom: Vector2 = Vector2.ONE
var previous_target_position: Vector2 = Vector2.ZERO
var target_velocity: Vector2 = Vector2.ZERO
var velocity_history: Array[Vector2] = []
var current_look_ahead: Vector2 = Vector2.ZERO
var damage_flash_layer: CanvasLayer = null
var damage_flash_overlay: ColorRect = null
var damage_flash_tween: Tween = null

func _ready() -> void:
	noise.seed = randi()
	noise.frequency = 0.3
	noise.fractal_octaves = 3

	position_smoothing_enabled = true
	position_smoothing_speed = camera_speed

	# The player's motion-comfort setting overrides whatever this instance
	# was authored with in the scene — that's the whole point of exposing
	# it as a real settings toggle instead of a fixed per-scene default.
	var settings = get_node_or_null("/root/GameSettings")
	if settings:
		steady_cam = settings.SteadyCamEnabled

	if target_path:
		target = get_node(target_path)
		if target:
			previous_target_position = target.global_position
			_connect_damage_feedback()

	_setup_damage_flash_overlay()

	zoom = default_zoom
	target_zoom = default_zoom

	velocity_history.resize(5)
	for i in range(velocity_history.size()):
		velocity_history[i] = Vector2.ZERO

func _process(delta: float) -> void:
	if target == null:
		return

	calculate_smooth_velocity(delta)
	follow_target_smoothly(delta)
	handle_zoom_smoothly(delta)

	if !steady_cam:
		apply_screen_shake(delta)
	else:
		offset = Vector2.ZERO
		rotation = 0.0

func calculate_smooth_velocity(delta: float) -> void:
	var raw_velocity = (target.global_position - previous_target_position) / delta
	previous_target_position = target.global_position

	velocity_history.pop_back()
	velocity_history.push_front(raw_velocity)

	var filtered_velocity = Vector2.ZERO
	var total_weight = 0.0
	for i in range(velocity_history.size()):
		var weight = velocity_history.size() - i
		filtered_velocity += velocity_history[i] * weight
		total_weight += weight

	if total_weight > 0:
		filtered_velocity /= total_weight

	# Deadzone to prevent micro-movements from tiny idle drift.
	if filtered_velocity.length() < 5.0:
		filtered_velocity = Vector2.ZERO

	target_velocity = filtered_velocity

func follow_target_smoothly(delta: float) -> void:
	var target_pos = target.global_position

	var offset_from_last_position = target_pos - global_position
	if offset_from_last_position.length() < deadzone_size.length() and !steady_cam:
		return

	var desired_look_ahead = Vector2.ZERO
	if !steady_cam:
		var normalized_velocity = target_velocity.normalized()
		var velocity_length = target_velocity.length()

		# sqrt scaling: less aggressive than linear so look-ahead doesn't
		# snap out too far at high speed.
		var scaled_velocity = sqrt(velocity_length) * normalized_velocity
		var raw_look_ahead = scaled_velocity * look_ahead_factor

		if raw_look_ahead.length() > max_look_ahead:
			raw_look_ahead = raw_look_ahead.normalized() * max_look_ahead

		desired_look_ahead = raw_look_ahead

	var look_ahead_weight = delta * (1.0 / look_ahead_smoothing)
	current_look_ahead = current_look_ahead.lerp(desired_look_ahead, look_ahead_weight)
	global_position = target_pos + current_look_ahead

func handle_zoom_smoothly(delta: float) -> void:
	var zoom_change = target_zoom - zoom
	var eased_zoom_change = zoom_change * (1.0 - exp(-zoom_speed * delta))
	zoom += eased_zoom_change

func apply_screen_shake(delta: float) -> void:
	trauma = max(trauma - delta * trauma_reduction_rate, 0.0)

	if trauma > 0.0:
		var shake_intensity = pow(trauma, trauma_exponent)
		var time = Time.get_ticks_msec() / 1000.0

		var noise_x = noise.get_noise_2d(time * noise_shake_speed, 0)
		var noise_y = noise.get_noise_2d(0, time * noise_shake_speed)
		var shake_time = time * noise_shake_speed * 0.8
		var noise_r = noise.get_noise_2d(shake_time, shake_time)

		# Non-linear response makes the shake feel more organic than raw noise.
		noise_x = sign(noise_x) * pow(abs(noise_x), 0.8)
		noise_y = sign(noise_y) * pow(abs(noise_y), 0.8)

		offset.x = noise_x * shake_intensity * max_shake_offset.x
		offset.y = noise_y * shake_intensity * max_shake_offset.y
		rotation = noise_r * shake_intensity * max_shake_rotation
	else:
		offset = offset.lerp(Vector2.ZERO, delta * 10.0)
		rotation = lerp(rotation, 0.0, delta * 10.0)

func _connect_damage_feedback() -> void:
	if target == null or !target.has_node("HealthComponent"):
		return

	var health = target.get_node("HealthComponent")
	var callback = Callable(self, "_on_player_took_damage")
	if health.has_signal("took_damage") and !health.is_connected("took_damage", callback):
		health.connect("took_damage", callback)

func _setup_damage_flash_overlay() -> void:
	damage_flash_layer = CanvasLayer.new()
	damage_flash_layer.name = "DamageFlashLayer"
	damage_flash_layer.layer = 120
	add_child(damage_flash_layer)

	damage_flash_overlay = ColorRect.new()
	damage_flash_overlay.name = "DamageFlashOverlay"
	damage_flash_overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	damage_flash_overlay.set_anchors_preset(Control.PRESET_FULL_RECT)
	var start_color = damage_flash_color
	start_color.a = 0.0
	damage_flash_overlay.color = start_color
	damage_flash_layer.add_child(damage_flash_overlay)

func _on_player_took_damage(_amount: int, _knockback_direction: Vector2) -> void:
	shake_camera(damage_shake_trauma, 0.12)
	flash_damage()

func flash_damage() -> void:
	if damage_flash_overlay == null:
		return

	if damage_flash_tween and damage_flash_tween.is_running():
		damage_flash_tween.kill()

	damage_flash_overlay.color = damage_flash_color
	damage_flash_tween = create_tween()
	damage_flash_tween.tween_property(damage_flash_overlay, "color:a", 0.0, damage_flash_duration) \
		.set_trans(Tween.TRANS_QUAD).set_ease(Tween.EASE_OUT)

# ===== Public API =====

func add_trauma(amount: float) -> void:
	trauma = min(trauma + amount, max_trauma)

func shake_camera(intensity: float, duration: float = 0.5) -> void:
	add_trauma(intensity)
	var tween = create_tween()
	tween.tween_method(func(t): add_trauma(intensity * (1.0 - t) * 0.2), 0.0, 1.0, duration)

func set_zoom_level(level: float) -> void:
	var new_zoom = clamp(level, min_zoom, max_zoom)
	target_zoom = Vector2(new_zoom, new_zoom)

func toggle_steady_cam(enable: bool) -> void:
	steady_cam = enable
	if steady_cam:
		trauma = 0.0
		offset = Vector2.ZERO
		rotation = 0.0
