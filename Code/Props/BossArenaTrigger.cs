using Godot;

// Marks a boss arena's camera cage. Combat music and the boss health bar
// are fully automatic now (see EnemyBase.gd's is_boss/_set_boss_combat_active
// — any flagged boss triggers both the instant it leaves idle/patrol, no
// trigger volume required). This is purely the spatial part nothing else
// can infer on its own: when the player enters the room, their camera gets
// caged to the arena bounds; on exit, it's released. Same Area2D/layer=0/
// mask=2/"body is Sam" convention as Checkpoint.cs/LevelExitDoor.cs.
public partial class BossArenaTrigger : Area2D
{
	// World-space camera limits for the fight — drawn from the room's own
	// bounds by hand rather than derived from this node's collision shape,
	// so the trigger volume (where the cage engages) and the camera cage
	// itself don't have to be the same rectangle.
	[Export] public float ArenaLeft;
	[Export] public float ArenaRight;
	[Export] public float ArenaTop;
	[Export] public float ArenaBottom;

	// Matches Camera2D's own built-in defaults (Godot ships -10000000/
	// 10000000 as the "unlimited" limit values) — restoring exactly those
	// rather than some arbitrary large number keeps "unlocked" indistinguishable
	// from a camera that was never limited at all.
	private const int Unlimited = 10000000;

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not Sam sam) return;
		if (sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;
		LockCamera(sam, true);
	}

	private void OnBodyExited(Node2D body)
	{
		if (body is not Sam sam) return;
		if (sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;
		LockCamera(sam, false);
	}

	private void LockCamera(Sam sam, bool locked)
	{
		Camera2D camera = sam.GetNodeOrNull<Camera2D>("PlayerCamera");
		if (camera == null) return;

		if (locked)
		{
			camera.LimitLeft = (int)ArenaLeft;
			camera.LimitRight = (int)ArenaRight;
			camera.LimitTop = (int)ArenaTop;
			camera.LimitBottom = (int)ArenaBottom;
		}
		else
		{
			camera.LimitLeft = -Unlimited;
			camera.LimitRight = Unlimited;
			camera.LimitTop = -Unlimited;
			camera.LimitBottom = Unlimited;
		}
	}
}
