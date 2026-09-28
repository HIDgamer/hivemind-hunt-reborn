using Godot;

// One of the N stations gating a Door2 — a level designer places
// Door2.RequiredPuzzleCount of these around a room, each pointed at the
// same door via TargetDoorPath but each configured with a different
// PuzzleScene (Terminal Bypass / Pipe Patch / Power Reroute), so opening
// the door genuinely takes solving several different minigames rather than
// the same one repeated. TaskStationBase's own one-shot lock already stops
// a single gate from being re-solved for extra credit.
public partial class DoorPuzzleGate : TaskStationBase
{
	[Export] public NodePath TargetDoorPath;

	private Door2 _door;

	public override void _Ready()
	{
		// _door must be resolved BEFORE base._Ready() — see
		// TerminalBypassStation.cs's identical comment for why.
		_door = GetNodeOrNull<Door2>(TargetDoorPath);
		if (_door == null)
		{
			GD.PushWarning($"DoorPuzzleGate '{Name}': TargetDoorPath does not resolve to a Door2.");
		}
		base._Ready();
	}

	protected override void OnSolved()
	{
		_door?.NotifyPuzzleSolved();
	}
}
