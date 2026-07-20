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

@export_group("Voice")
@export var alert_sound: AudioStream
@export var attack_sound: AudioStream
@export var hurt_sound: AudioStream
@export var death_sound: AudioStream
@export var death_pitch: float = 0.75

const GRAVITY: float = 1200.0

var _player: CharacterBody2D = null
var _face_dir: int = 1
var _is_dead: bool = false

var _contact_area: Area2D
var _hurtbox: Area2D
var _health: Node
var _voice: AudioStreamPlayer2D
var _sprite: AnimatedSprite2D
var locomotion: Node = null  # EnemyLocomotion, if the scene has one (see EnemyLocomotion.gd)


func _enemy_base_ready() -> void:
	var players := get_tree().get_nodes_in_group("Player")
	if players.size() > 0:
		_player = players[0]

	_sprite = get_node_or_null("AnimatedSprite2D")
	_contact_area = get_node_or_null("ContactArea")
	if _contact_area:
		_contact_area.body_entered.connect(_on_contact_entered)
		_contact_area.body_exited.connect(_on_contact_exited)

	_hurtbox = get_node_or_null("Hurtbox")
	if _hurtbox:
		_hurtbox.body_entered.connect(_on_hurtbox_body_entered)

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


func _face_towards(dx: float) -> void:
	if absf(dx) < 0.5 or _sprite == null:
		return
	_face_dir = 1 if dx > 0.0 else -1
	_sprite.flip_h = _face_dir < 0


# Mario-style top-down stomp. Subclasses that want a different vulnerability
# window (e.g. a boss only stompable while stunned after a whiffed attack)
# should toggle can_be_stomped themselves rather than overriding this.
func _on_hurtbox_body_entered(body: Node2D) -> void:
	if body != _player or _is_dead or not can_be_stomped:
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


# Overridable hooks — default to no-ops so subclasses only implement what
# their state machine actually needs.
func _on_contact_entered(_body: Node2D) -> void:
	pass


func _on_contact_exited(_body: Node2D) -> void:
	pass


func _on_self_damaged(_amount: int, _knockback: Vector2) -> void:
	pass


func _on_self_died() -> void:
	_is_dead = true
	_play_voice(death_sound, death_pitch)
	_set_combat_areas_enabled(false)
