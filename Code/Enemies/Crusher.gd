extends EnemyBase

# Mini-boss: heavy, proximity-aggro melee brute. Unlike Runner (blind,
# sound-triggered), Crusher notices the player by distance/line-of-sight and
# leans on two heavy-hitting moves once it closes in — a telegraphed charge
# (dash-lunge, covers real ground) and a close-range stomp (AOE ground-slam).
# A whiffed charge leaves it briefly stunned and stompable, the same
# "attack, miss, vulnerable" rhythm classic 2D minibosses use — and the one
# place this enemy is actually damageable outside of environmental hazards.
#
# Asset note: only Walk/Charge/Stomp/Dead sheets exist right now (no
# dedicated Idle or Stunned art). Idle and Stunned both hold frame 0 of Walk
# — same fallback approach Runner already uses for Dormant. Swap in real art
# later with no code changes; see the SpriteFrames setup in Crusher.tscn.

enum State { PATROL, CHASE, CHARGE, STOMP, STUNNED, DEAD }

@export_group("Movement")
@export var patrol_speed: float = 70.0
@export var chase_speed: float = 130.0
@export var patrol_range: float = 80.0

@export_group("Aggro")
@export var detect_radius: float = 260.0
@export var charge_range: float = 220.0
@export var stomp_range: float = 60.0

@export_group("Charge")
@export var charge_speed: float = 480.0
@export var charge_duration: float = 0.5
@export var charge_cooldown: float = 2.2
@export var charge_damage: int = 30
@export var whiff_stun_duration: float = 1.1

@export_group("Stomp")
@export var stomp_attack_damage: int = 25
@export var stomp_attack_cooldown: float = 1.8
@export var stomp_windup: float = 0.35

const INVESTIGATE_DURATION: float = 5.0
const NAV_ARRIVE_DISTANCE: float = 14.0

var current_state: State = State.PATROL
var _spawn_pos: Vector2
var _last_seen_pos: Vector2
var _investigate_timer: float = 0.0
var _charge_timer: float = 0.0
var _charge_cooldown_timer: float = 0.0
var _charge_dir: float = 1.0
var _hit_player_this_charge: bool = false
var _stomp_timer: float = 0.0
var _stomp_cooldown_timer: float = 0.0
var _stomp_landed: bool = false
var _stun_timer: float = 0.0
var _patrol_dir: int = 1
var _player_in_contact: bool = false
var _edge_ray: RayCast2D
var _nav_agent: NavigationAgent2D


func _ready() -> void:
	_enemy_base_ready()
	_spawn_pos = global_position
	_last_seen_pos = global_position

	_edge_ray = get_node_or_null("EdgeRayCast2D")
	_nav_agent = get_node_or_null("NavigationAgent2D")
	if _nav_agent:
		await get_tree().physics_frame

	$AnimatedSprite2D.animation_finished.connect(_on_animation_finished)
	_change_state(State.PATROL)


func _physics_process(delta: float) -> void:
	if not is_on_floor():
		velocity.y += GRAVITY * delta
	elif velocity.y > 0.0:
		velocity.y = 0.0

	if _charge_cooldown_timer > 0.0:
		_charge_cooldown_timer -= delta
	if _stomp_cooldown_timer > 0.0:
		_stomp_cooldown_timer -= delta

	match current_state:
		State.PATROL:
			_patrol_tick(delta)
		State.CHASE:
			_chase_tick(delta)
		State.CHARGE:
			_charge_tick(delta)
		State.STOMP:
			_stomp_tick(delta)
		State.STUNNED:
			_stunned_tick(delta)
		State.DEAD:
			velocity.x = 0.0

	move_and_slide()
	_update_sprite()


func _patrol_tick(delta: float) -> void:
	_check_aggro()

	var target_x := _spawn_pos.x + _patrol_dir * patrol_range
	var past_turn_point := (
		(_patrol_dir > 0 and global_position.x >= target_x) or
		(_patrol_dir < 0 and global_position.x <= target_x)
	)
	if past_turn_point or _edge_ahead():
		_patrol_dir *= -1
		velocity.x = 0.0
		return

	velocity.x = _patrol_dir * patrol_speed


func _chase_tick(delta: float) -> void:
	_check_aggro()

	var dist_to_player := INF
	if is_instance_valid(_player):
		dist_to_player = global_position.distance_to(_player.global_position)

	if dist_to_player <= stomp_range and _stomp_cooldown_timer <= 0.0:
		_change_state(State.STOMP)
		return

	if dist_to_player <= charge_range and _charge_cooldown_timer <= 0.0:
		_change_state(State.CHARGE)
		return

	var dist_to_target := global_position.distance_to(_last_seen_pos)
	if dist_to_target <= NAV_ARRIVE_DISTANCE:
		velocity.x = move_toward(velocity.x, 0.0, chase_speed)
		_investigate_timer -= delta
		if _investigate_timer <= 0.0:
			_change_state(State.PATROL)
		return

	if _nav_agent == null:
		var dx := _last_seen_pos.x - global_position.x
		velocity.x = sign(dx) * chase_speed
		return

	if locomotion:
		velocity.x = locomotion.compute_chase_velocity(self, _last_seen_pos, chase_speed)
		if locomotion.jump_this_frame() and is_on_floor():
			velocity.y = locomotion.jump_velocity
	else:
		_nav_agent.target_position = _last_seen_pos
		if not _nav_agent.is_navigation_finished():
			var next_pos := _nav_agent.get_next_path_position()
			velocity.x = sign(next_pos.x - global_position.x) * chase_speed


func _charge_tick(_delta: float) -> void:
	_charge_timer -= _delta
	velocity.x = _charge_dir * charge_speed

	if _charge_timer <= 0.0:
		_charge_cooldown_timer = charge_cooldown
		if _hit_player_this_charge:
			_change_state(State.CHASE)
		else:
			# Whiffed — brief vulnerability window before resuming the hunt.
			_change_state(State.STUNNED)


func _stomp_tick(delta: float) -> void:
	velocity.x = move_toward(velocity.x, 0.0, chase_speed * 2.0)
	_stomp_timer -= delta
	if _stomp_timer <= 0.0 and not _stomp_landed:
		_resolve_stomp()


func _stunned_tick(delta: float) -> void:
	velocity.x = move_toward(velocity.x, 0.0, chase_speed * 2.0)
	_stun_timer -= delta
	if _stun_timer <= 0.0:
		if is_instance_valid(_player) and _investigate_timer > 0.0:
			_change_state(State.CHASE)
		else:
			_change_state(State.PATROL)


func _check_aggro() -> void:
	if not is_instance_valid(_player):
		return
	var dist := global_position.distance_to(_player.global_position)
	if dist <= detect_radius:
		_last_seen_pos = _player.global_position
		_investigate_timer = INVESTIGATE_DURATION
		if current_state == State.PATROL:
			_change_state(State.CHASE)


func _resolve_stomp() -> void:
	_stomp_landed = true
	_stomp_cooldown_timer = stomp_attack_cooldown
	if is_instance_valid(_player):
		var dist := global_position.distance_to(_player.global_position)
		if dist <= stomp_range:
			var health := _player.get_node_or_null("HealthComponent")
			if health:
				var knockback := (_player.global_position - global_position).normalized()
				health.Damage(stomp_attack_damage, knockback)


func _on_contact_entered(body: Node2D) -> void:
	if body != _player:
		return
	_player_in_contact = true
	if current_state == State.CHARGE and not _hit_player_this_charge:
		_hit_player_this_charge = true
		var health := body.get_node_or_null("HealthComponent")
		if health:
			var knockback := (body.global_position - global_position).normalized()
			health.Damage(charge_damage, knockback)


func _on_contact_exited(body: Node2D) -> void:
	if body != _player:
		return
	_player_in_contact = false


func _on_self_damaged(_amount: int, _knockback: Vector2) -> void:
	if is_instance_valid(_player):
		_last_seen_pos = _player.global_position
		_investigate_timer = INVESTIGATE_DURATION
	if current_state != State.CHARGE and current_state != State.STOMP:
		_change_state(State.CHASE)


func _on_self_died() -> void:
	super._on_self_died()
	_change_state(State.DEAD)


func _change_state(new_state: State) -> void:
	if current_state == new_state:
		return
	current_state = new_state
	match new_state:
		State.PATROL:
			$AnimatedSprite2D.play("Walk")
		State.CHASE:
			$AnimatedSprite2D.play("Walk")
		State.CHARGE:
			_charge_timer = charge_duration
			_hit_player_this_charge = false
			_charge_dir = float(_face_dir)
			if is_instance_valid(_player):
				_charge_dir = signf(_player.global_position.x - global_position.x)
				if _charge_dir == 0.0:
					_charge_dir = float(_face_dir)
			$AnimatedSprite2D.play("Charge")
			_play_voice(attack_sound)
		State.STOMP:
			_stomp_timer = stomp_windup
			_stomp_landed = false
			velocity.x = 0.0
			$AnimatedSprite2D.play("Stomp")
		State.STUNNED:
			_stun_timer = whiff_stun_duration
			velocity.x = 0.0
			$AnimatedSprite2D.play("Idle")
		State.DEAD:
			velocity = Vector2.ZERO
			$AnimatedSprite2D.play("Dead")
			_set_combat_areas_enabled(false)


func _update_sprite() -> void:
	if current_state == State.DEAD or current_state == State.STUNNED:
		return
	if velocity.x > 0.5:
		_face_dir = 1
		$AnimatedSprite2D.flip_h = false
	elif velocity.x < -0.5:
		_face_dir = -1
		$AnimatedSprite2D.flip_h = true


func _edge_ahead() -> bool:
	if _edge_ray == null:
		return false
	_edge_ray.target_position = Vector2(_patrol_dir * 20.0, 40.0)
	_edge_ray.force_raycast_update()
	return not _edge_ray.is_colliding()


func _on_animation_finished() -> void:
	var sprite: AnimatedSprite2D = $AnimatedSprite2D
	if sprite.animation == "Stomp" and current_state == State.STOMP:
		if not _stomp_landed:
			_resolve_stomp()
		_change_state(State.CHASE if _investigate_timer > 0.0 else State.PATROL)
