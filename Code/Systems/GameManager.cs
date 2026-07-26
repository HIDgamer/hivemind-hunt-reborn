using Godot;

// Single discoverable place documenting "these are this level's world
// systems" — GasSimulation, EnvironmentDamage, FluidSimulation,
// LineOfSightSystem, ObjectManager each already expose their own static
// Instance, set in _EnterTree() (see the matching comment on each of those)
// specifically so every one of them is guaranteed non-null by the time any
// sibling node's _Ready() runs, regardless of scene declaration order.
// GameManager doesn't change that contract or own any of their state — it's
// a single named entry point for it, and the natural place future code
// looks first instead of hunting down five separate class names.
//
// Deliberately just pass-through properties: no game-state enum, no pause
// coordination, no "systems ready" signal. Nothing today needs to wait past
// the _EnterTree phase (every consumer either belongs to the same initial
// scene batch as the systems above, safe already, or is spawned later at
// runtime, safe by construction since the systems were already long since
// initialized) — so none of that would do anything but sit unused.
// PauseMenu/SaveManager/CheckpointManager are already-working autoloads;
// wrapping them here would be indirection over something that isn't broken.
public partial class GameManager : Node
{
	public static GameManager Instance { get; private set; }

	public GasSimulation GasSimulation => GasSimulation.Instance;
	public EnvironmentDamage EnvironmentDamage => EnvironmentDamage.Instance;
	public FluidSimulation FluidSimulation => FluidSimulation.Instance;
	public LineOfSightSystem LineOfSight => LineOfSightSystem.Instance;
	public ObjectManager ObjectManager => ObjectManager.Instance;

	// Same _EnterTree()-not-_Ready() reasoning as the world systems this
	// wraps — guarantees GameManager.Instance is just as safely available to
	// anything else in the same scene batch.
	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}
}
