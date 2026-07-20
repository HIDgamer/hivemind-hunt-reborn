class_name EnemyLocomotion
extends Node

# Composed (not inherited) onto an EnemyBase-derived enemy that has a
# NavigationAgent2D sibling. Turns "walk toward the next path point" into
# "walk OR jump toward the next path point", so a chase can cross a gap that
# NavLinkGenerator bridged with a NavigationLink2D without the enemy needing
# any of Sam's actual jump/dash code — this is a capability PROFILE (how far
# can this specific enemy's hop reach), not a movement port.
#
# Reference tuning: Sam's own Jump (Code/Characters/Sam.cs) uses
# JumpVelocity=-315, Gravity=780 — jump_velocity/GRAVITY below default close
# to that so a generated link sized for "a player jump" is also reachable by
# a default-tuned enemy. Raise jump_velocity/max_jump_gap per-enemy (e.g. for
# a specific level's harder gaps), don't fight the defaults level by level.

const GRAVITY: float = 1200.0  # matches EnemyBase.GRAVITY — kept separate so
                                 # this file has no hard coupling to EnemyBase.

@export var jump_velocity: float = -420.0
@export var max_jump_gap: float = 160.0  # horizontal reach used by NavLinkGenerator
@export var can_dash: bool = false  # Crusher's charge doubles as a traversal dash
@export var dash_speed: float = 420.0

var _nav_agent: NavigationAgent2D
var _jump_queued: bool = false


func _ready() -> void:
	_nav_agent = get_parent().get_node_or_null("NavigationAgent2D")
	if _nav_agent == null:
		push_warning("%s: EnemyLocomotion needs a sibling NavigationAgent2D." % get_parent().name)


# Call once per physics tick while chasing. Returns the desired horizontal
# velocity; also sets should_jump.y < 0 as an out-value via jump_this_frame()
# — GDScript has no out-params, so callers should check jump_this_frame()
# immediately after calling this in the same tick.
func compute_chase_velocity(body: CharacterBody2D, target_position: Vector2, speed: float) -> float:
	_jump_queued = false
	if _nav_agent == null:
		return signf(target_position.x - body.global_position.x) * speed

	_nav_agent.target_position = target_position
	if _nav_agent.is_navigation_finished():
		return 0.0

	var next_pos: Vector2 = _nav_agent.get_next_path_position()
	var to_next: Vector2 = next_pos - body.global_position

	# A meaningfully upward next-point while grounded means the path is
	# routing across a NavigationLink2D jump-link rather than along flat
	# ground — walking won't cross it, so jump instead. (Godot doesn't expose
	# "is this corner a link" directly on NavigationAgent2D in a stable way
	# across versions; the height-delta heuristic is robust and cheap, and
	# false positives just mean an extra hop on a steep-but-walkable ramp,
	# which is harmless.)
	if body.is_on_floor() and to_next.y < -20.0 and absf(to_next.y) > absf(to_next.x) * 0.5:
		_jump_queued = true

	var dir: float = signf(to_next.x)
	if dir == 0.0:
		dir = signf(target_position.x - body.global_position.x)
	return dir * speed


func jump_this_frame() -> bool:
	return _jump_queued


# Solves whether a gap of the given horizontal/vertical delta is reachable
# with this profile's jump arc at the given approach speed — used by
# NavLinkGenerator when deciding whether to bridge two ledges for THIS
# enemy's capability profile.
func can_reach_gap(horizontal_gap: float, vertical_delta: float, approach_speed: float) -> bool:
	if can_dash and horizontal_gap <= dash_speed * 0.5:
		return true
	if horizontal_gap > max_jump_gap:
		return false

	# vertical_delta > 0 means the target ledge is BELOW the start (falling
	# onto it is easy); < 0 means it's above (must still be rising when it
	# arrives). Solve v0*t + 0.5*g*t^2 = vertical_delta for t, take the
	# later (physically meaningful) root, then check horizontal reach in
	# that time at approach_speed.
	var v0: float = jump_velocity
	var g: float = GRAVITY
	var discriminant: float = v0 * v0 + 2.0 * g * vertical_delta
	if discriminant < 0.0:
		return false  # can't get high enough to reach a ledge this far above
	var t: float = (-v0 + sqrt(discriminant)) / g
	if t <= 0.0:
		return false
	return approach_speed * t >= horizontal_gap
