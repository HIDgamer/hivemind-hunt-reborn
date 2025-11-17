extends CharacterBody2D

# Completely blind — hunts by sound alone.
# Sounds it reacts to: player landing, sprinting, walking too close, dashing,
# being hurt (scream), and a nearby box getting hit by a laser.
#
# Collision layers (see Project Settings > Layer Names > 2D Physics):
#   Runner occupies layer 4 ("Enemies") only, and its collision_mask is set
#   to layer 1 ("World") only — it stands on the level geometry but never
#   solid-body-collides with the player. All player-facing detection goes
#   through the two Area2D children below instead:
#     - ContactArea: wakes/alerts the Runner and gates the bite attack.
#     - Hurtbox: where the *Runner* takes damage — currently a top-down
#       stomp (Mario-style) from the player, and automatically vulnerable
#       to anything else that finds its HealthComponent (e.g. Laser.cs).
#
# For laser-box detection to work, pushable RigidBody2D objects should be in
# the group "pushable". They don't need a HealthComponent — see _on_laser_hit.
# Laser nodes must be in the group "laser".
#
# Threading note: pathfinding queries go through NavigationAgent2D, which is
# already resolved off the main thread by Godot's own NavigationServer2D —
# there's nothing left here worth moving onto a manual Thread/WorkerThreadPool
# job. With only one Runner in the level, hand-rolled threading would add
# real risk (GDScript/Node access from worker threads is not safe by default)
# for no measurable win. If the roster grows to many simultaneous enemies,
# the sound-detection pass (_check_sounds_active) is the one loop worth
# batching onto WorkerThreadPool — it's cheap per-enemy today, so left as is.

enum State { DORMANT, PATROL, CHASE, ATTACK, STUNNED, FLEE, DEAD }

@export_group("Movement")
@export var patrol_speed: float = 110.0
@export var chase_speed: float = 200.0
@export var patrol_range: float = 96.0

@export_group("Hearing")
@export var sprint_hear_radius: float = 300.0
@export var landing_hear_radius: float = 240.0
@export var dash_hear_radius: float = 190.0
@export var walk_hear_radius: float = 68.0
@export var hurt_hear_radius: float = 430.0
@export var laser_box_hear_radius: float = 260.0
@export var sneak_threshold: float = 82.0
@export var sprint_threshold: float = 160.0
@export var wake_run_speed: float = 120.0

@export_group("Combat")
@export var bite_damage: int = 15
@export var bite_cooldown: float = 1.1
@export var attack_lunge_speed: float = 320.0
@export var stun_duration: float = 1.4
@export var stomp_damage: int = 40
@export var stomp_bounce_force: float = 420.0
@export var stomp_height_margin: float = 12.0

@export_group("Tutorial")
@export var tutorial_mode: bool = false

@export_group("Voice")
@export var alert_sound: AudioStream
@export var attack_sound: AudioStream
@export var hurt_sound: AudioStream
@export var death_sound: AudioStream
@export var death_pitch: float = 0.75

const GRAVITY: float = 1200.0
const INVESTIGATE_DURATION: float = 4.5
const PATROL_PAUSE: float = 1.2
const WAKE_PAUSE: float = 0.45
const NAV_ARRIVE_DISTANCE: float = 12.0

var current_state: State = State.DORMANT
var _player: CharacterBody2D = null
var _spawn_pos: Vector2
var _last_heard_pos: Vector2
var _investigate_timer: float = 0.0
var _patrol_pause_timer: float = 0.0
var _wake_timer: float = 0.0
var _attack_cooldown_timer: float = 0.0
var _stun_timer: float = 0.0
var _patrol_dir: int = 1
var _face_dir: int = 1
var _player_in_contact: bool = false
var _contact_area: Area2D
var _hurtbox: Area2D
var _edge_ray: RayCast2D
var _nav_agent: NavigationAgent2D
var _health: Node
var _voice: AudioStreamPlayer2D


func _ready() -> void:
	_spawn_pos = global_position
	_last_heard_pos = global_position

	var players := get_tree().get_nodes_in_group("Player")
	if players.size() > 0:
		_player = players[0]
		_hook_player_signals()

	_hook_laser_signals()
	get_tree().node_added.connect(_on_node_added)

	_contact_area = get_node_or_null("ContactArea")
	if _contact_area:
		_contact_area.body_entered.connect(_on_contact_entered)
		_contact_area.body_exited.connect(_on_contact_exited)
	else:
		push_warning("Runner: ContactArea not found — touch detection won't work.")

	_hurtbox = get_node_or_null("Hurtbox")
	if _hurtbox:
		_hurtbox.body_entered.connect(_on_hurtbox_body_entered)
	else:
		push_warning("Runner: Hurtbox not found — the player won't be able to stomp this enemy.")

	_health = get_node_or_null("HealthComponent")
	if _health:
		if _health.has_signal("took_damage"):
			_health.took_damage.connect(_on_self_damaged)
		if _health.has_signal("died"):
			_health.died.connect(_on_self_died)
	else:
		push_warning("Runner: HealthComponent not found — this enemy cannot take damage.")

	_edge_ray = get_node_or_null("EdgeRayCast2D")
	_voice = get_node_or_null("VoiceAudio")

	_nav_agent = get_node_or_null("NavigationAgent2D")
	if _nav_agent:
		_nav_agent.velocity_computed.connect(_on_nav_velocity_computed)
		# Let NavigationServer2D finish its first sync before the first query.
		await get_tree().physics_frame

	$AnimatedSprite2D.animation_finished.connect(_on_animation_finished)

	if tutorial_mode:
		_change_state(State.PATROL)
	else:
		_change_state(State.DORMANT)


func _hook_player_signals() -> void:
	if not is_instance_valid(_player):
		return
	if _player.has_signal("landed"):
		_player.landed.connect(_on_player_landed)

	var health := _player.get_node_or_null("HealthComponent")
	if health and health.has_signal("took_damage"):
		health.took_damage.connect(_on_player_hurt)

	var dash := _player.get_node_or_null("DashComponent")
	if dash and dash.has_signal("dash_started"):
		dash.dash_started.connect(_on_player_dashed)


func _hook_laser_signals() -> void:
	for node in get_tree().get_nodes_in_group("laser"):
		_try_connect_laser(node)


func _try_connect_laser(node: Node) -> void:
	if node.has_signal("laser_hit") and not node.laser_hit.is_connected(_on_laser_hit):
		node.laser_hit.connect(_on_laser_hit)


func _on_node_added(node: Node) -> void:
	if node.is_in_group("laser"):
		_try_connect_laser(node)


func _physics_process(delta: float) -> void:
	if not is_on_floor():
		velocity.y += GRAVITY * delta
	elif velocity.y > 0.0:
		velocity.y = 0.0

	if _attack_cooldown_timer > 0.0:
		_attack_cooldown_timer -= delta

	if _wake_timer > 0.0:
		_wake_timer -= delta
		velocity.x = 0.0
		move_and_slide()
		return

	match current_state:
		State.DORMANT:
			velocity.x = 0.0
		State.PATROL:
			_patrol_tick(delta)
		State.CHASE:
			_chase_tick(delta)
		State.ATTACK:
			_attack_tick(delta)
		State.STUNNED:
			_stunned_tick(delta)
		State.FLEE:
			_flee_tick(delta)
		State.DEAD:
			velocity.x = 0.0

	move_and_slide()
	_update_sprite()


func _patrol_tick(delta: float) -> void:
	_check_sounds_active()

	if _patrol_pause_timer > 0.0:
		_patrol_pause_timer -= delta
		velocity.x = 0.0
		return

	var target_x := _spawn_pos.x + _patrol_dir * patrol_range
	var past_turn_point := (
		(_patrol_dir > 0 and global_position.x >= target_x) or
		(_patrol_dir < 0 and global_position.x <= target_x)
	)

	if past_turn_point or _edge_ahead():
		_patrol_dir *= -1
		_patrol_pause_timer = PATROL_PAUSE
		velocity.x = 0.0
		return

	velocity.x = _patrol_dir * patrol_speed


# Chase uses NavigationAgent2D so the Runner follows the baked level nav mesh
# instead of blindly walking toward the target's raw x position — it won't
# march off a ledge or grind against a wall it has no path around.
func _chase_tick(delta: float) -> void:
	_check_sounds_active()

	if _player_in_contact and _attack_cooldown_timer <= 0.0:
		_change_state(State.ATTACK)
		return

	var dist_to_target := global_position.distance_to(_last_heard_pos)
	if dist_to_target <= NAV_ARRIVE_DISTANCE:
		velocity.x = move_toward(velocity.x, 0.0, chase_speed)
		_investigate_timer -= delta
		if _investigate_timer <= 0.0:
			_change_state(State.PATROL)
		return

	if _nav_agent == null:
		# Fallback if the nav agent is ever missing from a variant scene.
		var dx := _last_heard_pos.x - global_position.x
		velocity.x = sign(dx) * chase_speed
		return

	_nav_agent.target_position = _last_heard_pos
	if _nav_agent.is_navigation_finished():
		# Either arrived, or the target is unreachable from here (e.g. the
		# player is up on a ledge this ground-bound hunter can't climb to).
		# Either way, stop and wait out the investigate timer instead of
		# idling in place forever — it gives up and resumes patrol like any
		# other lost trail.
		velocity.x = move_toward(velocity.x, 0.0, chase_speed)
		_investigate_timer -= delta
		if _investigate_timer <= 0.0:
			_change_state(State.PATROL)
		return

	var next_pos := _nav_agent.get_next_path_position()
	var dir: float = sign(next_pos.x - global_position.x)
	if dir == 0.0:
		dir = sign(_last_heard_pos.x - global_position.x)

	var desired_velocity := Vector2(dir * chase_speed, velocity.y)
	if _nav_agent.avoidance_enabled:
		_nav_agent.set_velocity(desired_velocity)
	else:
		velocity.x = desired_velocity.x


func _attack_tick(_delta: float) -> void:
	velocity.x = move_toward(velocity.x, 0.0, chase_speed * 2.0)


func _stunned_tick(delta: float) -> void:
	velocity.x = move_toward(velocity.x, 0.0, chase_speed * 2.0)
	_stun_timer -= delta
	if _stun_timer <= 0.0:
		if is_instance_valid(_player) and _investigate_timer > 0.0:
			_change_state(State.CHASE)
		else:
			_change_state(State.PATROL)


func _flee_tick(_delta: float) -> void:
	if not is_instance_valid(_player):
		_change_state(State.PATROL)
		return

	var away_dir: float = sign(global_position.x - _player.global_position.x)
	if away_dir == 0:
		away_dir = _face_dir
	velocity.x = away_dir * chase_speed

	if global_position.distance_to(_spawn_pos) > patrol_range * 4.0:
		velocity.x = 0.0
		_change_state(State.PATROL)


# Runs every frame when in PATROL or CHASE.
# Sneaking (below sneak_threshold) near the runner without touching it won't trigger.
# Any contact in these states triggers immediately, even while sneaking.

func _check_sounds_active() -> void:
	if not is_instance_valid(_player):
		return

	var dist := global_position.distance_to(_player.global_position)
	var pspeed := _player.velocity.length()

	if _player_in_contact:
		_alert(_player.global_position)
		return

	if pspeed >= sprint_threshold and dist <= sprint_hear_radius:
		_alert(_player.global_position)
		return

	if pspeed > sneak_threshold and dist <= walk_hear_radius:
		_alert(_player.global_position)


# Signal handlers — player sounds

func _on_player_landed(impact_speed: float) -> void:
	if current_state == State.DORMANT:
		return
	if not is_instance_valid(_player):
		return
	if impact_speed < 90.0:
		return
	var dist := global_position.distance_to(_player.global_position)
	if dist <= landing_hear_radius:
		_alert(_player.global_position)


func _on_player_hurt(_amount: int, _knockback: Vector2) -> void:
	# Pain scream is loud enough to wake a dormant runner
	if not is_instance_valid(_player):
		return
	var dist := global_position.distance_to(_player.global_position)
	if dist <= hurt_hear_radius:
		_alert(_player.global_position)


func _on_player_dashed(_direction: Vector2) -> void:
	if current_state == State.DORMANT:
		return
	if not is_instance_valid(_player):
		return
	var dist := global_position.distance_to(_player.global_position)
	if dist <= dash_hear_radius:
		_alert(_player.global_position)


# Laser hitting a pushable box makes a distinct metallic clang.
# Note: Laser.cs only emits LaserHit when a HealthComponent is found on the
# target. If your boxes don't have one, either add a HealthComponent to them
# (high max health so they survive) or modify Laser.cs to also emit for
# bodies in the "pushable" group regardless of health.

func _on_laser_hit(target: Node2D) -> void:
	if not is_instance_valid(target):
		return
	if not target.is_in_group("pushable"):
		return
	var dist := global_position.distance_to(target.global_position)
	if dist <= laser_box_hear_radius:
		_alert(target.global_position)


# Contact area signals — also gates the bite attack in _chase_tick.

func _on_contact_entered(body: Node2D) -> void:
	if body != _player:
		return
	_player_in_contact = true

	if current_state == State.DORMANT:
		var pspeed := _player.velocity.length()
		if pspeed >= wake_run_speed:
			_alert(_player.global_position)


func _on_contact_exited(body: Node2D) -> void:
	if body != _player:
		return
	_player_in_contact = false


# Hurtbox — this is where the Runner *receives* damage, currently only from
# a top-down stomp (the player falling onto it from above). Other damage
# sources (e.g. Laser.cs) find HealthComponent directly and bypass this area
# entirely, which is intentional — see the collision-layer note up top.

func _on_hurtbox_body_entered(body: Node2D) -> void:
	if body != _player or current_state == State.DEAD:
		return
	if not ("velocity" in body):
		return

	var is_stomp: bool = (
		body.global_position.y < global_position.y - stomp_height_margin
		and body.velocity.y > 0.0
	)
	if not is_stomp:
		return

	body.velocity.y = -stomp_bounce_force

	if is_instance_valid(_health) and not _health.IsDead:
		_health.Damage(stomp_damage, Vector2.UP)


# Own-health signals — non-lethal hits stun (or, in the tutorial, make the
# runner flee); a lethal hit kills it outright.

func _on_self_damaged(_amount: int, _knockback: Vector2) -> void:
	_play_voice(hurt_sound)
	if tutorial_mode:
		_change_state(State.FLEE)
	else:
		_change_state(State.STUNNED)


func _on_self_died() -> void:
	_play_voice(death_sound, death_pitch)
	_change_state(State.DEAD)


# Core alert — sets destination and switches to CHASE.
# Tutorial runners ignore this entirely. Combat/death states can't be
# interrupted by a fresh alert.

func _alert(heard_at: Vector2) -> void:
	if tutorial_mode:
		return
	if current_state == State.DEAD or current_state == State.STUNNED or current_state == State.ATTACK:
		return

	_last_heard_pos = heard_at
	_investigate_timer = INVESTIGATE_DURATION

	if current_state == State.DORMANT:
		_wake_timer = WAKE_PAUSE
		_play_voice(alert_sound)

	_change_state(State.CHASE)


func _change_state(new_state: State) -> void:
	if current_state == new_state:
		return
	current_state = new_state
	match new_state:
		State.DORMANT:
			$AnimatedSprite2D.play("Dormant")
			velocity = Vector2.ZERO
		State.PATROL:
			$AnimatedSprite2D.play("Walk")
		State.CHASE:
			$AnimatedSprite2D.play("Walk")
		State.ATTACK:
			$AnimatedSprite2D.play("Lunge")
			velocity.x = _face_dir * attack_lunge_speed
			_play_voice(attack_sound)
		State.STUNNED:
			$AnimatedSprite2D.play("Stunned")
			_stun_timer = stun_duration
			velocity.x = 0.0
		State.FLEE:
			$AnimatedSprite2D.play("Walk")
		State.DEAD:
			$AnimatedSprite2D.play("Dead")
			velocity = Vector2.ZERO
			_set_combat_areas_enabled(false)


func _play_voice(stream: AudioStream, pitch: float = 1.0) -> void:
	if _voice == null or stream == null:
		return
	_voice.stream = stream
	_voice.pitch_scale = pitch
	_voice.play()


func _set_combat_areas_enabled(enabled: bool) -> void:
	if _contact_area:
		_contact_area.monitoring = enabled
		_contact_area.monitorable = enabled
	if _hurtbox:
		_hurtbox.monitoring = enabled
		_hurtbox.monitorable = enabled


func _update_sprite() -> void:
	var sprite: AnimatedSprite2D = $AnimatedSprite2D

	if current_state == State.DEAD:
		return

	if tutorial_mode and is_instance_valid(_player):
		var dir : float = sign(_player.global_position.x - global_position.x)
		if dir != 0:
			_face_dir = int(dir)
			sprite.flip_h = _face_dir < 0
		return

	if current_state == State.DORMANT or current_state == State.STUNNED:
		return

	if velocity.x > 0.5:
		_face_dir = 1
		sprite.flip_h = false
	elif velocity.x < -0.5:
		_face_dir = -1
		sprite.flip_h = true


func _on_nav_velocity_computed(safe_velocity: Vector2) -> void:
	if current_state == State.CHASE:
		velocity.x = safe_velocity.x


# Returns true if there's no floor in front of the runner (patrol only, not chase)

func _edge_ahead() -> bool:
	if _edge_ray == null:
		return false
	_edge_ray.target_position = Vector2(_face_dir * 18.0, 36.0)
	_edge_ray.force_raycast_update()
	return not _edge_ray.is_colliding()


# Resolves the bite once the Lunge animation completes — deals damage only
# if the player is still in contact when the swing lands.

func _on_animation_finished() -> void:
	var sprite: AnimatedSprite2D = $AnimatedSprite2D
	if sprite.animation != "Lunge" or current_state != State.ATTACK:
		return

	_attack_cooldown_timer = bite_cooldown

	if is_instance_valid(_player) and _player_in_contact:
		var health := _player.get_node_or_null("HealthComponent")
		if health:
			var knockback := (_player.global_position - global_position).normalized()
			health.Damage(bite_damage, knockback)

	_change_state(State.CHASE)
