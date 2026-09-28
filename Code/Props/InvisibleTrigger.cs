using Godot;

// A generic, invisible Area2D trigger volume — walking into it (and,
// optionally, out of it) just emits a signal. What actually happens is
// entirely up to what's wired to Triggered/Exited in the editor's Node >
// Signals panel (open a door via Powered(true), call a hazard's Break(),
// start a cutscene through DialogueUI, play an AnimationPlayer, all of the
// above at once, etc.) — Godot signals support multiple simultaneous
// connections, so one trigger can genuinely drive several different things
// without this script needing to know what any of them are.
//
// No sprite by design (it's meant to be invisible in actual play) — resize
// the CollisionShape2D per placement to whatever area should count.
public partial class InvisibleTrigger : Area2D
{
	// Fires once and then never again — the common case for a one-time trap
	// spring or cutscene start. Turn off for something meant to retrigger
	// every time the player walks back through (an ambient trigger, a
	// repeatable trap).
	[Export] public bool OneShot = true;

	// In multiplayer, only the locally-authoritative Sam should fire this —
	// otherwise a remote player's replicated position would also trigger it
	// on every other peer's own machine. Same convention as LevelExitDoor/
	// BossArenaTrigger/Checkpoint.
	[Export] public bool RequireLocalPlayer = true;

	private bool _triggered;

	[Signal] public delegate void TriggeredEventHandler();
	[Signal] public delegate void ExitedEventHandler();

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (_triggered && OneShot) return;
		if (body is not Sam sam) return;
		if (RequireLocalPlayer && sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;

		_triggered = true;
		EmitSignal(SignalName.Triggered);
	}

	private void OnBodyExited(Node2D body)
	{
		if (body is not Sam sam) return;
		if (RequireLocalPlayer && sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;

		EmitSignal(SignalName.Exited);
	}
}
