extends EnemyBase

# Final-boss encounter. Unlike Runner/Crusher, Queen doesn't patrol — she
# holds her ground until the player is close enough to notice, then
# alternates approach/headbutt with a whiff-and-punish rhythm: land the
# headbutt telegraph and she recovers clean; miss it and she's briefly
# stunned and Mario-stompable, same "reward good positioning" pattern
# Crusher's charge already establishes, just with a slower, heavier read.
#
# Two-phase fight, driven entirely by tuning numbers (no extra art needed):
# past phase_2_health_ratio the telegraph shortens and the attack repeats
# faster — same Headbutt animation, just less time to react and less
# downtime between attempts.
#
# Also a ranged caster: past headbutt_range but within spit_range she lobs
# an AcidSpit (Code/Enemies/AcidSpit.gd) — a particle-driven projectile, not
# a sprite bullet, since no acid-spit art exists. Headbutt and Spit have
# independent cooldowns so she genuinely alternates rather than picking
# whichever one happens to be off cooldown.
#
# Lucy.gd (the "hardcore" variant) extends this directly and only overrides
# the @export stat block + swaps idle/walk/stunned/death textures — see
# Lucy.gd for why no separate behavior tree was needed.

enum State { IDLE, APPROACH, HEADBUTT, SPIT, STUNNED, DEAD }

const AcidSpitScene := preload("res://Scenes/Characters/AcidSpit.tscn")

@export_group("Movement")
@export var approach_speed: float = 90.0

@export_group("Aggro")
@export var detect_radius: float = 320.0
@export var headbutt_range: float = 90.0

@export_group("Headbutt")
@export var headbutt_telegraph: float = 0.6
@export var headbutt_lunge_speed: float = 260.0
@export var headbutt_duration: float = 0.45
@export var headbutt_damage: int = 35
@export var headbutt_cooldown: float = 1.6
@export var whiff_stun_duration: float = 1.3

@export_group("Acid Spit")
@export var spit_range: float = 280.0
@export var spit_telegraph: float = 0.5
@export var spit_damage: int = 20
@export var spit_projectile_speed: float = 260.0
@export var spit_cooldown: float = 2.4

@export_group("Phase 2")
@export var phase_2_health_ratio: float = 0.5
@export var phase_2_telegraph_scale: float = 0.55
@export var phase_2_cooldown_scale: float = 0.6

var current_state: State = State.IDLE
var _telegraph_timer: float = 0.0
var _headbutt_timer: float = 0.0
var _headbutt_dir: float = 1.0
var _hit_player_this_headbutt: bool = false
var _cooldown_timer: float = 0.0
var _spit_cooldown_timer: float = 0.0
var _stun_timer: float = 0.0
var _in_phase_2: bool = false
var _player_in_contact: bool = false

# Optional Marker2D child (place one named "SpitOrigin" in the scene, e.g.
# at the mouth) so the spit's spawn point can be positioned per-scene
# instead of a hardcoded body-relative offset. Falls back to a fixed offset
# above the body if the scene doesn't have one. Positioned in the editor for
# the default (facing-right) pose — _set_face_dir() mirrors its local X
# whenever facing flips, so it stays on the correct side of the face.
@onready var _spit_origin: Node2D = get_node_or_null("SpitOrigin")
var _spit_origin_base_x: float = 0.0


func _ready() -> void:
	_enemy_base_ready()
	if _spit_origin:
		_spit_origin_base_x = absf(_spit_origin.position.x)
	if _health and _health.has_signal("HealthChanged"):
		_health.HealthChanged.connect(_on_health_changed)
	$AnimatedSprite2D.animation_finished.connect(_on_animation_finished)
	_change_state(State.IDLE)


func _physics_process(delta: float) -> void:
	if not _enemy_base_physics_process(delta):
		return

	if not is_on_floor():
		velocity.y += GRAVITY * delta
	elif velocity.y > 0.0:
		velocity.y = 0.0

	if _cooldown_timer > 0.0:
		_cooldown_timer -= delta
	if _spit_cooldown_timer > 0.0:
		_spit_cooldown_timer -= delta

	match current_state:
		State.IDLE:
			_idle_tick()
		State.APPROACH:
			_approach_tick(delta)
		State.HEADBUTT:
			_headbutt_tick(delta)
		State.SPIT:
			_spit_tick(delta)
		State.STUNNED:
			_stunned_tick(delta)
		State.DEAD:
			velocity.x = 0.0

	move_and_slide()
	_update_sprite()


func _idle_tick() -> void:
	velocity.x = 0.0
	if not is_instance_valid(_player):
		return
	if global_position.distance_to(_player.global_position) <= detect_radius:
		_change_state(State.APPROACH)


func _approach_tick(_delta: float) -> void:
	if not is_instance_valid(_player):
		velocity.x = 0.0
		return

	var dist := global_position.distance_to(_player.global_position)
	if dist <= headbutt_range and _cooldown_timer <= 0.0:
		_change_state(State.HEADBUTT)
		return

	# Too far to headbutt but close enough to spit — stop closing the
	# distance and lob acid instead of always marching all the way in.
	if dist > headbutt_range and dist <= spit_range and _spit_cooldown_timer <= 0.0:
		_change_state(State.SPIT)
		return

	var dx := _player.global_position.x - global_position.x
	if absf(dx) <= headbutt_range * 0.5:
		velocity.x = 0.0
	else:
		velocity.x = signf(dx) * approach_speed


func _spit_tick(delta: float) -> void:
	velocity.x = 0.0
	if _telegraph_timer <= 0.0:
		return
	_telegraph_timer -= delta
	if _telegraph_timer <= 0.0:
		_launch_acid_spit()
		var cooldown_scale := phase_2_cooldown_scale if _in_phase_2 else 1.0
		_spit_cooldown_timer = spit_cooldown * cooldown_scale
		_change_state(State.APPROACH)


# Known multiplayer gap, deliberately out of scope for this pass: this only
# ever runs on the authority (see _enemy_base_physics_process), so the spit
# node only exists in the SERVER's own scene tree — it isn't spawner-managed,
# so it never replicates to clients at all. Queen's melee/position/health
# sync clients correctly; her ranged attack will be invisible on clients
# until AcidSpit is converted to go through a proper MultiplayerSpawner.
func _launch_acid_spit() -> void:
	if not is_instance_valid(_player):
		return
	var spit := AcidSpitScene.instantiate()
	get_tree().current_scene.add_child(spit)
	var fallback_origin := global_position + Vector2(0, -20)
	var origin: Vector2 = _spit_origin.global_position if _spit_origin else fallback_origin
	var dir: Vector2 = (_player.global_position - origin).normalized()
	if dir == Vector2.ZERO:
		dir = Vector2(float(_face_dir), 0.0)
	spit.damage = spit_damage
	spit.speed = spit_projectile_speed
	spit.launch(origin, dir)
	_play_voice(attack_sound)


func _headbutt_tick(_delta: float) -> void:
	if _telegraph_timer > 0.0:
		_telegraph_timer -= _delta
		velocity.x = 0.0
		if _telegraph_timer <= 0.0:
			_headbutt_timer = headbutt_duration
			velocity.x = _headbutt_dir * headbutt_lunge_speed
		return

	_headbutt_timer -= _delta
	velocity.x = _headbutt_dir * headbutt_lunge_speed
	if _headbutt_timer <= 0.0:
		_cooldown_timer = headbutt_cooldown * (phase_2_cooldown_scale if _in_phase_2 else 1.0)
		if _hit_player_this_headbutt:
			_change_state(State.APPROACH)
		else:
			_change_state(State.STUNNED)


func _stunned_tick(delta: float) -> void:
	velocity.x = move_toward(velocity.x, 0.0, approach_speed * 2.0)
	_stun_timer -= delta
	if _stun_timer <= 0.0:
		_change_state(State.APPROACH)


func _on_contact_entered(body: Node2D) -> void:
	if not body.is_in_group("Player"):
		return
	_player_in_contact = true
	var mid_swing := current_state == State.HEADBUTT and _telegraph_timer <= 0.0
	if mid_swing and not _hit_player_this_headbutt:
		_hit_player_this_headbutt = true
		var health := body.get_node_or_null("HealthComponent")
		if health:
			var knockback := (body.global_position - global_position).normalized()
			health.Damage(headbutt_damage, knockback)


func _on_contact_exited(body: Node2D) -> void:
	if not body.is_in_group("Player"):
		return
	_player_in_contact = false


func _on_health_changed(current: int, max_health: int) -> void:
	if max_health <= 0:
		return
	var ratio := float(current) / float(max_health)
	if not _in_phase_2 and ratio <= phase_2_health_ratio:
		_in_phase_2 = true


func _on_self_damaged(_amount: int, _knockback: Vector2) -> void:
	if current_state == State.IDLE:
		_change_state(State.APPROACH)


func _on_self_died() -> void:
	super._on_self_died()
	_change_state(State.DEAD)


func _change_state(new_state: State) -> void:
	if current_state == new_state:
		return
	current_state = new_state
	match new_state:
		State.IDLE:
			$AnimatedSprite2D.play("Idle")
		State.APPROACH:
			$AnimatedSprite2D.play("Walk")
		State.HEADBUTT:
			var telegraph_scale := phase_2_telegraph_scale if _in_phase_2 else 1.0
			_telegraph_timer = headbutt_telegraph * telegraph_scale
			_hit_player_this_headbutt = false
			_headbutt_dir = float(_face_dir)
			if is_instance_valid(_player):
				_headbutt_dir = signf(_player.global_position.x - global_position.x)
				if _headbutt_dir == 0.0:
					_headbutt_dir = float(_face_dir)
			$AnimatedSprite2D.play("Headbutt")
			_play_voice(attack_sound)
		State.SPIT:
			_telegraph_timer = spit_telegraph * (phase_2_telegraph_scale if _in_phase_2 else 1.0)
			if is_instance_valid(_player):
				var dx := _player.global_position.x - global_position.x
				if dx != 0.0:
					_set_face_dir(1 if dx > 0.0 else -1)
			# No dedicated spit-windup art — Idle's loop holds the telegraph,
			# same fallback approach used elsewhere for missing animations.
			$AnimatedSprite2D.play("Idle")
		State.STUNNED:
			_stun_timer = whiff_stun_duration
			velocity.x = 0.0
			$AnimatedSprite2D.play("Stunned")
		State.DEAD:
			velocity = Vector2.ZERO
			$AnimatedSprite2D.play("Dead")
			_set_combat_areas_enabled(false)


func _update_sprite() -> void:
	if current_state == State.DEAD or current_state == State.STUNNED:
		return
	if velocity.x > 0.5:
		_set_face_dir(1)
	elif velocity.x < -0.5:
		_set_face_dir(-1)


# Central point for changing which way she's facing — keeps the sprite flip
# and the SpitOrigin marker's mirrored X in lockstep so the spit always
# leaves from the correct side of the face regardless of facing direction.
func _set_face_dir(dir: int) -> void:
	_face_dir = dir
	$AnimatedSprite2D.flip_h = _face_dir < 0
	if _spit_origin:
		_spit_origin.position.x = _spit_origin_base_x * _face_dir


func _on_animation_finished() -> void:
	pass  # Headbutt's timing is driven by _telegraph_timer/_headbutt_timer, not clip length.
