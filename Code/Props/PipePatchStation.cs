using Godot;

// Interact near an actively-leaking GasVent to seal it via the Pipe Patch
// minigame. Only prompts while the target vent IsBroken — a vent that's
// already fine has nothing to fix.
public partial class PipePatchStation : TaskStationBase
{
	[Export] public NodePath TargetVentPath;

	private GasVent _vent;

	public override void _Ready()
	{
		// _vent must be resolved BEFORE base._Ready() — see
		// TerminalBypassStation.cs's identical comment for why.
		_vent = GetNodeOrNull<GasVent>(TargetVentPath);
		if (_vent == null)
		{
			GD.PushWarning($"PipePatchStation '{Name}': TargetVentPath does not resolve to a GasVent.");
		}
		base._Ready();
	}

	protected override bool IsAvailable() => _vent != null && _vent.IsBroken;

	// The vent can break again later (GasVent.Break() is a public method
	// other systems call), so this station must stay usable every time it
	// does — IsAvailable() already re-gates correctly on IsBroken either way.
	protected override bool AllowRetrigger => true;

	protected override void OnSolved()
	{
		_vent?.Repair();
	}
}
