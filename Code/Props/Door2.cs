using Godot;

// Multi-puzzle door: stays closed until RequiredPuzzleCount different
// DoorPuzzleGate stations have each independently solved their own puzzle
// (see DoorPuzzleGate.cs) — a level designer places that many stations,
// each pointed at this same door and each configured with a different
// PuzzleScene (Terminal Bypass / Pipe Patch / Power Reroute), so the door
// genuinely takes 3 separate minigames rather than one repeated 3 times.
// Opens once and stays open permanently — there's no reason to re-lock a
// door once every gate has already been solved.
public partial class Door2 : DoorBase
{
	[Export] public int RequiredPuzzleCount = 3;

	private int _solvedCount;

	protected override void OnDoorReady()
	{
		if (WasTriggeredBefore())
		{
			CurrentState = DoorState.Open;
			AnimatedSprite.Animation = "IdleOpen";
			AnimatedSprite.Play();
			CollisionShape.Disabled = true;
			return;
		}

		AnimatedSprite.Animation = "IdleClose";
		AnimatedSprite.Play();
		CollisionShape.Disabled = false;
	}

	// Called by each DoorPuzzleGate station once its own puzzle solves.
	// Safe to call more times than RequiredPuzzleCount (a re-solved gate
	// that somehow re-triggers just no-ops once the door's already open) —
	// including the replay each gate's own TaskStationBase._Ready() does
	// when restoring a previously-solved gate on level reload.
	public void NotifyPuzzleSolved()
	{
		if (IsNetClient() || CurrentState != DoorState.Closed) return;

		_solvedCount++;
		if (_solvedCount >= RequiredPuzzleCount)
		{
			StartOpening();
			MarkTriggered();
		}
	}
}
