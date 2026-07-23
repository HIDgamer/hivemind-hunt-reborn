extends Area2D

# Kill volume for falling off the level. Was listening for area_entered,
# but Sam is a CharacterBody2D (a body, not an Area2D) so this almost
# certainly never fired in practice. Now respawns at the player's last
# checkpoint instead of unconditionally reloading the whole scene.

func _ready() -> void:
	body_entered.connect(_on_body_entered)

func _on_body_entered(body: Node2D) -> void:
	if not body.is_in_group("Player"):
		return
	# A remote puppet's replicated position crosses this same Area2D locally
	# on every peer's machine, not just the one that actually owns that
	# player — RespawnPlayer() always repositions THIS machine's own local
	# authority Sam, so without this guard any peer falling dragged every
	# other peer's own player back to the checkpoint too. Only the machine
	# that actually owns the body which fell may react to it.
	if body.get("IsNetworked") and not body.IsMultiplayerAuthority():
		return
	get_node("/root/CheckpointManager").RespawnPlayer()
