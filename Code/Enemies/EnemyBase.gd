class_name EnemyBase
extends CharacterBody2D

# Shared foundation for every enemy (Runner, Crusher, Queen, Lucy). Holds the
# parts that were proven out in Runner.gd and don't vary per-enemy: health/
# hurtbox/contact-area wiring, voice playback, sprite facing, the top-down
# stomp-kill interaction, and a shared "move toward a nav target" helper that
# understands EnemyLocomotion's jump links. What DOES vary — the actual state
# machine (sound-stalker vs. boss-arena vs. charge-and-slam miniboss) — stays
# in each subclass; this is composition of the common plumbing, not a forced
# shared behavior tree.
#
# Threading: deliberately none. NavigationServer2D already resolves pathing
# off the main thread (confirmed — see Runner.gd's original note, still true
# here), and project.godot currently caps worker_pool/max_threads=1 anyway,
# so a hand-rolled WorkerThreadPool job would have zero parallelism headroom
# until that's raised. With a roster in the single digits per level, the
# per-frame cost of sound/distance checks here is trivial; if the roster ever
# grows large, the per-enemy _check_sounds_active-style loop is the one worth
# batching — not something to preemptively thread now.

@export_group("Combat")
@export var can_be_stomped: bool = true
@export var stomp_damage: int = 40
@export var stomp_bounce_force: float = 420.0
@export var stomp_height_margin: float = 12.0
# Multiplier applied when the stomp lands on WeakPoint instead of Hurtbox —
# an optional second Area2D subclasses can add to their scene (e.g. Crusher/
# Queen/Lucy positioning one on their back or head) for a real "aim for the
# weak point" reward during a vulnerability window, rather than every stomp
# anywhere on the body dealing the same flat damage.
@export var weak_point_damage_multiplier: float = 2.0

@export_group("Voice")
@export var alert_sound: AudioStream
@export var attack_sound: AudioStream
@export var hurt_sound: AudioStream
@export var death_sound: AudioStream
@export var death_pitch: float = 0.75

@export_group("Boss")
# Flagging an enemy as a boss makes combat music + the shared boss health
# bar fully automatic — no manually-placed trigger volume needed. A
# subclass just calls _set_boss_combat_active(bool) whenever its own state
# machine leaves/re-enters idle/patrol (see Crusher.gd/Queen.gd). Runner
# stays a plain enemy (is_boss defaults false) — day-to-day fodder should
# never summon boss music just by aggroing.
@export var is_boss: bool = false
@export var boss_display_name: String = ""
@export var boss_music: AudioStream
var _boss_combat_active: bool = false

const GRAVITY: float = 1200.0
const TARGET_REFRESH_INTERVAL: float = 0.25

var _player: CharacterBody2D = null
var _face_dir: int = 1
var _is_dead: bool = false
var _target_refresh_timer: float = 0.0

var _contact_area: Area2D
var _hurtbox: Area2D
var _weak_point: Area2D
var _health: Node
var _voice: AudioStreamPlayer2D
var _sprite: AnimatedSprite2D
var locomotion: Node = null  # EnemyLocomotion, if the scene has one (see EnemyLocomotion.gd)

# Mirrors HealthComponent.CurrentHealth (a C# property with a private set,
# so it can't be a MultiplayerSynchronizer target directly) into a plain
# exported var a synchronizer CAN replicate — same trick Sam.cs already uses
# for NetAnimationName/NetFrame/NetFlipH. Only the authority writes this;
# every other peer just reads whatever value arrives over the wire.
@export var net_current_health: int = 0


# Enemies are static per-scene children present identically in every peer's
# own loaded copy of the level (same shape as a single-player pre-placed
# Sam) rather than spawner-managed — so there's no dynamic authority
# handoff to figure out, it's always the server. Harmless no-op offline.
func _enemy_base_ready() -> void:
	# Reloading the same level (e.g. backtracking through an exit door)
	# would otherwise resurrect every enemy killed on a previous visit —
	# LevelStateManager remembers which ones died so this one can just
	# remove itself immediately instead of spawning in alive.
	var level_state = get_node_or_null("/root/LevelStateManager")
	if level_state and level_state.WasEnemyDead(_current_scene_path(), level_state.StableKeyFor(self)):
		queue_free()
		return

	set_multiplayer_authority(1)
	_refresh_target()

	_sprite = get_node_or_null("AnimatedSprite2D")
	_contact_area = get_node_or_null("ContactArea")
	if _contact_area:
		_contact_area.body_entered.connect(_on_contact_entered)
		_contact_area.body_exited.connect(_on_contact_exited)

	_hurtbox = get_node_or_null("Hurtbox")
	if _hurtbox:
		_hurtbox.body_entered.connect(_on_hurtbox_body_entered)

	_weak_point = get_node_or_null("WeakPoint")
	if _weak_point:
		_weak_point.body_entered.connect(_on_weak_point_body_entered)

	_health = get_node_or_null("HealthComponent")
	if _health:
		if _health.has_signal("TookDamage"):
			_health.TookDamage.connect(_on_self_damaged)
		if _health.has_signal("Died"):
			_health.Died.connect(_on_self_died)
	else:
		push_warning("%s: HealthComponent not found — this enemy cannot take damage." % name)

	_voice = get_node_or_null("VoiceAudio")
	locomotion = get_node_or_null("EnemyLocomotion")


# Call as the first line of every subclass's _physics_process, and bail
# (return) when it returns false: on a non-authority peer, position/
# animation/health arrive purely via MultiplayerSynchronizer, so running
# the state machine/move_and_slide locally too would just fight the
# replicated values. Also keeps _player refreshed to whichever real player
# is nearest right now, instead of a single reference picked once at
# _ready() (get_nodes_in_group("Player")[0] on that machine, previously) —
# which could be a remote puppet and could differ per peer entirely.
func _enemy_base_physics_process(delta: float) -> bool:
	if not _has_authority():
		return false
	_target_refresh_timer -= delta
	if _target_refresh_timer <= 0.0 or not is_instance_valid(_player):
		_refresh_target()
	if is_instance_valid(_health):
		net_current_health = _health.CurrentHealth
	return true


func _current_scene_path() -> String:
	var scene := get_tree().current_scene
	return scene.scene_file_path if scene else ""


# is_multiplayer_authority() alone is never trusted anywhere else in this
# codebase for exactly this reason — every other authority check (Runner.gd,
# ChatBox.cs, CheckpointManager.cs) explicitly short-circuits on "not
# networked at all" first rather than relying on Godot's offline peer-id
# default lining up the way you'd expect. Skipping that guard here meant a
# wrong assumption about that default would silently freeze every enemy's
# _physics_process in single-player — exactly what "AI completely broken"
# looked like.
func _has_authority() -> bool:
	var network_manager := get_node_or_null("/root/NetworkManager")
	if network_manager == null or not network_manager.IsNetworked:
		return true
	return is_multiplayer_authority()


func _refresh_target() -> void:
	_target_refresh_timer = TARGET_REFRESH_INTERVAL
	var nearest: CharacterBody2D = null
	var best_dist := INF
	for p in get_tree().get_nodes_in_group("Player"):
		if not is_instance_valid(p):
			continue
		var d := global_position.distance_squared_to(p.global_position)
		if d < best_dist:
			best_dist = d
			nearest = p
	_player = nearest


func _play_voice(stream: AudioStream, pitch: float = 1.0) -> void:
	if _voice == null or stream == null:
		return
	_voice.stream = stream
	_voice.pitch_scale = pitch
	_voice.play()


# Call whenever this boss's own state machine leaves/re-enters idle/patrol
# (see Crusher.gd/Queen.gd's _change_state). No-op for any enemy that isn't
# flagged is_boss — Runner aggroing never touches music/the boss bar at all.
func _set_boss_combat_active(active: bool) -> void:
	if not is_boss or active == _boss_combat_active:
		return
	_boss_combat_active = active

	var music_mgr := get_node_or_null("/root/MusicManager")
	var health_bar := get_node_or_null("/root/BossHealthBar")

	if active:
		if music_mgr and boss_music:
			music_mgr.EnterCombat(boss_music)
		if health_bar:
			var display_name: String = boss_display_name if boss_display_name != "" else String(name)
			health_bar.call("show_for", _health, display_name)
	else:
		if music_mgr:
			music_mgr.ExitCombat()
		if health_bar:
			health_bar.call("hide_bar")


# Always called via set_deferred rather than direct assignment — this runs
# from _on_self_died(), which itself typically runs from inside a
# body_entered/exited callback (a lethal stomp resolving mid-signal). Godot
# blocks monitoring/monitorable writes while an Area2D's in/out signals are
# still being dispatched and logs "Function blocked during in/out signal"
# instead of applying them — deferring sidesteps that entirely.
func _set_combat_areas_enabled(enabled: bool) -> void:
	if _contact_area:
		_contact_area.set_deferred("monitoring", enabled)
		_contact_area.set_deferred("monitorable", enabled)
	if _hurtbox:
		_hurtbox.set_deferred("monitoring", enabled)
		_hurtbox.set_deferred("monitorable", enabled)
	if _weak_point:
		_weak_point.set_deferred("monitoring", enabled)
		_weak_point.set_deferred("monitorable", enabled)


func _face_towards(dx: float) -> void:
	if absf(dx) < 0.5 or _sprite == null:
		return
	_face_dir = 1 if dx > 0.0 else -1
	_sprite.flip_h = _face_dir < 0


# Mario-style top-down stomp. Subclasses that want a different vulnerability
# window (e.g. a boss only stompable while stunned after a whiffed attack)
# should toggle HealthComponent.ExternallyInvulnerable themselves — Damage()
# already respects it, so this needs no per-state awareness at all.
#
# Runs unguarded on every peer (like Laser.cs/SparkHazard.cs's damage
# checks) — this Hurtbox exists identically in every peer's own loaded copy
# of the enemy, so every peer's local physics independently detects any
# player (local or puppet) touching it. Bouncing the stomping player is
# harmless to run everywhere (a puppet's velocity being set is inert, only
# the real local player's velocity matters). Damaging the ENEMY, however,
# must only ever happen once — see the authority guard below.
func _on_hurtbox_body_entered(body: Node2D) -> void:
	_try_stomp(body, 1.0)


# WeakPoint is an optional second Area2D (see weak_point_damage_multiplier)
# a subclass's scene can add for a stomp that rewards precision — same stomp
# geometry check, just a bigger payoff. A no-op for any enemy scene that
# doesn't have a WeakPoint node at all (see _enemy_base_ready).
func _on_weak_point_body_entered(body: Node2D) -> void:
	_try_stomp(body, weak_point_damage_multiplier)


func _try_stomp(body: Node2D, damage_multiplier: float) -> void:
	if _is_dead or not can_be_stomped or not body.is_in_group("Player"):
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

	if _has_authority() and is_instance_valid(_health) and not _health.IsDead:
		_health.Damage(int(round(stomp_damage * damage_multiplier)), Vector2.UP)


# Overridable hooks — default to no-ops so subclasses only implement what
# their state machine actually needs.
func _on_contact_entered(_body: Node2D) -> void:
	pass


func _on_contact_exited(_body: Node2D) -> void:
	pass


func _on_self_damaged(_amount: int, _knockback: Vector2) -> void:
	pass


# Called on a Punch/Kick that rolls its (fairly low) stun chance — see
# Sam.cs's ApplyAttackHit, which already skips this entirely for a hit that
# didn't actually land (dead/invulnerable target). Default no-op: Crusher and
# Queen already have their own STUNNED state (until now only entered by their
# own whiffed attack) and override this to reuse it via _change_state, rather
# than tracking a second, competing stun timer here. Runner already stuns on
# every non-lethal hit via its own _on_self_damaged, so it needs no override
# at all. An enemy with neither (or one with no vulnerability window at all)
# just shrugs the stagger off.
func apply_stun(_duration: float) -> void:
	pass


func _on_self_died() -> void:
	_is_dead = true
	_play_voice(death_sound, death_pitch)
	_set_combat_areas_enabled(false)

	var level_state = get_node_or_null("/root/LevelStateManager")
	if level_state:
		level_state.MarkEnemyDead(_current_scene_path(), level_state.StableKeyFor(self))
