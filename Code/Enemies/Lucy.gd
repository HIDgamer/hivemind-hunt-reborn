extends "res://Code/Enemies/Queen.gd"

# "Hardcore" Queen — same fight, same state machine, just meaner numbers and
# her own art (idle/walk/dead swapped, "crit" pose flashes in place of a
# generic Stunned hold). No new behavior tree: the whole identity is a
# stat block + a hit-reaction flash, layered on top of Queen.gd via GDScript
# script inheritance rather than duplicating the state machine.
#
# Scene defaults (Lucy.tscn) already retune movement/combat @export values
# tighter than Queen's own — this script only adds the crit-flash visual,
# which has no equivalent in the base Queen fight since Queen's "crit" art
# doesn't exist.

@export var crit_flash_duration: float = 0.25

var _crit_flash_timer: float = 0.0


func _physics_process(delta: float) -> void:
	super._physics_process(delta)
	if _crit_flash_timer > 0.0:
		_crit_flash_timer -= delta


func _on_self_damaged(_amount: int, _knockback: Vector2) -> void:
	_crit_flash_timer = crit_flash_duration
	super._on_self_damaged(_amount, _knockback)


func _update_sprite() -> void:
	if _crit_flash_timer > 0.0 and current_state != State.DEAD:
		$AnimatedSprite2D.play("Stunned")  # "crit" pose, held while the flash timer runs
		return
	super._update_sprite()
