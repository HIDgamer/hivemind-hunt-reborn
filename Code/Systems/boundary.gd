extends Area2D

# Kill volume for falling off the level. Was listening for area_entered,
# but Sam is a CharacterBody2D (a body, not an Area2D) so this almost
# certainly never fired in practice. Now respawns at the player's last
# checkpoint instead of unconditionally reloading the whole scene.

func _ready() -> void:
	body_entered.connect(_on_body_entered)

func _on_body_entered(body: Node2D) -> void:
	if body.is_in_group("Player"):
		get_node("/root/CheckpointManager").RespawnPlayer()
