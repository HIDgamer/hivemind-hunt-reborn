using Godot;

// Trap door: starts OPEN (the reverse of Door.cs's default) and closes the
// instant a player has fully passed through it — permanently. There's no
// re-opening; once sealed, this door stays sealed for the rest of the
// level. Closing on BodyExited rather than BodyEntered so the door doesn't
// visibly swing shut on top of the player mid-crossing — it waits until
// they've actually cleared the doorway.
public partial class Door3 : DoorBase
{
	private Area2D _area;
	private bool _triggered;

	protected override void OnDoorReady()
	{
		_area = GetNode<Area2D>("Area2D");
		_area.BodyExited += OnBodyExited;

		if (WasTriggeredBefore())
		{
			_triggered = true;
			CurrentState = DoorState.Closed;
			AnimatedSprite.Animation = "IdleClose";
			AnimatedSprite.Play();
			CollisionShape.Disabled = false;
			return;
		}

		CurrentState = DoorState.Open;
		AnimatedSprite.Animation = "IdleOpen";
		AnimatedSprite.Play();
		CollisionShape.Disabled = true;
	}

	private void OnBodyExited(Node2D body)
	{
		if (_triggered || IsNetClient()) return;
		if (body is not Sam sam) return;
		if (sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;

		_triggered = true;
		StartClosing();
		MarkTriggered();
	}
}
