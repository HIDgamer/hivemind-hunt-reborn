using Godot;

// Interact to solve a Terminal Bypass minigame whose sequence length scales
// with how scorched this terminal's own position currently is (a
// previously-burned terminal is a harder bypass), then unlocks the target
// Door via its existing Powered(bool) contract.
public partial class TerminalBypassStation : TaskStationBase
{
	[Export] public NodePath TargetDoorPath;

	private Door _door;

	public override void _Ready()
	{
		// _door must be resolved BEFORE base._Ready() — if this station was
		// already solved on a previous visit (LevelStateManager), the base
		// class's own _Ready() calls OnSolved() synchronously to reapply the
		// unlock to this freshly-reloaded Door. Resolving _door after that
		// point left it null exactly when the restore path needed it,
		// silently dropping the reapply — the door remembered nothing was
		// wrong, it just never got told to open.
		_door = GetNodeOrNull<Door>(TargetDoorPath);
		if (_door == null)
		{
			GD.PushWarning($"TerminalBypassStation '{Name}': TargetDoorPath does not resolve to a Door.");
		}
		base._Ready();
	}

	protected override Godot.Collections.Dictionary BuildConfig()
	{
		float scorch = EnvironmentDamage.Instance?.GetScorchAt(GlobalPosition) ?? 0f;
		int length = Mathf.Clamp(3 + Mathf.FloorToInt(scorch * 3f), 3, 6);
		return new Godot.Collections.Dictionary { { "length", length } };
	}

	protected override void OnSolved()
	{
		_door?.Powered(true);
	}
}
