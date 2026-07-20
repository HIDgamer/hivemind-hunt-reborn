extends Area2D

# Queen/Lucy's ranged attack — a acid glob that travels in a straight line,
# dripping particles the whole way, and pops in a splash burst on whatever
# it hits first (player or world geometry). Deliberately particle-driven
# rather than a sprite bullet, per the ask — there's no dedicated acid
# sprite art, and a travelling CPUParticles2D trail plus an impact burst
# reads as "spit" without needing any.
#
# Collision: mask covers World (wall/floor splat, no damage) and Player
# (damage + splash). Layer is 0 — nothing needs to detect the spit itself,
# only the spit needs to detect what it hits.

@export var speed: float = 260.0
@export var damage: int = 20
@export var lifetime: float = 2.5

var _direction: Vector2 = Vector2.RIGHT
var _life_timer: float = 0.0
var _spent: bool = false

@onready var _trail: CPUParticles2D = $TrailParticles
@onready var _body: Sprite2D = $Body

const BODY_BASE_SCALE := Vector2(0.16, 0.16)


func launch(from_position: Vector2, direction: Vector2) -> void:
	global_position = from_position
	_direction = direction.normalized()
	rotation = _direction.angle()
	_life_timer = lifetime


func _ready() -> void:
	body_entered.connect(_on_body_entered)
	# A round blob flying in a dead-straight line at speed reads as a laser,
	# not a lobbed glob of goo — a continuous squash/stretch jiggle sells the
	# "wobbling blob of liquid" read that the sprite alone can't.
	var jiggle := create_tween().set_loops()
	jiggle.tween_property(_body, "scale", BODY_BASE_SCALE * Vector2(1.25, 0.8), 0.09)
	jiggle.tween_property(_body, "scale", BODY_BASE_SCALE * Vector2(0.85, 1.2), 0.09)
	jiggle.tween_property(_body, "scale", BODY_BASE_SCALE, 0.09)


func _physics_process(delta: float) -> void:
	if _spent:
		return

	global_position += _direction * speed * delta
	_life_timer -= delta
	if _life_timer <= 0.0:
		_pop(false)


func _on_body_entered(body: Node2D) -> void:
	if _spent:
		return

	if body.is_in_group("Player"):
		var health := body.get_node_or_null("HealthComponent")
		if health:
			health.Damage(damage, _direction)
		_pop(true)
	else:
		# World geometry (a wall/floor) — splat without dealing damage.
		_pop(false)


func _pop(hit_player: bool) -> void:
	_spent = true
	_trail.emitting = false
	set_deferred("monitoring", false)

	var splash := CPUParticles2D.new()
	splash.emitting = true
	splash.one_shot = true
	splash.explosiveness = 1.0
	splash.amount = 10
	splash.lifetime = 0.4
	# Reuse the glob's own texture for the splat fragments so the impact
	# reads as "the same blob breaking apart" instead of generic smoke.
	splash.texture = _body.texture
	splash.color_ramp = _trail.color_ramp
	splash.spread = 180.0
	splash.gravity = Vector2(0, 220)
	splash.initial_velocity_min = 30.0
	splash.initial_velocity_max = 80.0
	splash.scale_amount_min = 0.05
	splash.scale_amount_max = 0.09
	splash.global_position = global_position
	get_parent().add_child(splash)
	get_tree().create_timer(1.0).timeout.connect(func():
		if is_instance_valid(splash):
			splash.queue_free()
	)

	queue_free()
