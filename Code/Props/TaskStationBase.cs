using Godot;

// Shared proximity+Interact-prompt base for Among-Us-style task-station
// props (PipePatchStation, PowerRerouteStation, TerminalBypassStation).
// Same proximity+debounce shape as NpcDialogueTrigger.cs — an Area2D tracks
// Sam entering/exiting range, polls Input.IsActionJustPressed("Interact")
// in _Process while in range, and opens TaskPuzzleUI (autoload) with this
// station's configured puzzle scene/title. Subclasses decide what "solved"
// means (repair a vent, power a section, unlock a door) via OnSolved(), and
// can hide the prompt entirely via IsAvailable() (e.g. a vent that isn't
// currently broken has nothing to fix).
public partial class TaskStationBase : Area2D
{
	[Export] public PackedScene PuzzleScene;
	[Export] public string Title = "TASK";

	private bool _playerInRange;
	private Sam _playerInRangeNode;
	// Same reasoning as NpcDialogueTrigger's own _waitingForRelease — the
	// press that opens this puzzle and the press the puzzle's own UI might
	// react to are read by different polling/handled-input consumers, so an
	// actual key release is required before a new press counts.
	private bool _waitingForRelease;
	// Set once the puzzle is solved for the first time — most stations are
	// one-and-done (a door unlocked, a section powered), so without this a
	// player could just walk up and replay the same puzzle forever. Override
	// AllowRetrigger for stations whose own IsAvailable() already governs
	// re-availability correctly (e.g. PipePatchStation, whose vent can break
	// again later).
	private bool _solved;

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;

		// Reloading the same level (e.g. backtracking through an exit door)
		// would otherwise forget this was already solved — LevelStateManager
		// remembers it, so re-solving is skipped AND the solved effect
		// (unlock the door, power the section, repair the vent) is reapplied
		// immediately, since whatever it targets was also freshly reset by
		// the reload.
		LevelStateManager levelState = GetNodeOrNull<LevelStateManager>("/root/LevelStateManager");
		string scenePath = GetTree().CurrentScene?.SceneFilePath;
		if (levelState != null && !string.IsNullOrEmpty(scenePath)
			&& levelState.WasStationSolved(scenePath, levelState.StableKeyFor(this)))
		{
			_solved = true;
			OnSolved();
		}
	}

	public override void _Process(double delta)
	{
		if (_waitingForRelease)
		{
			if (!Input.IsActionPressed("Interact")) _waitingForRelease = false;
			return;
		}

		if (!_playerInRange || _playerInRangeNode == null) return;
		// Otherwise typing "z" into chat (or any other UI capturing input)
		// could pop a puzzle open mid-message — Interact is polled here
		// independently of Sam's own gameplay-input gating.
		if (_playerInRangeNode.UiInputCaptured) return;
		if (_solved && !AllowRetrigger) return;
		if (!IsAvailable()) return;

		CanvasLayer taskUi = GetNodeOrNull<CanvasLayer>("/root/TaskPuzzleUI");
		if (taskUi == null || (bool)taskUi.Call("is_active")) return;

		CanvasLayer dialogueUi = GetNodeOrNull<CanvasLayer>("/root/DialogueUI");
		if (dialogueUi != null && (bool)dialogueUi.Call("is_active")) return;

		if (Input.IsActionJustPressed("Interact"))
		{
			OpenPuzzle(taskUi);
			_waitingForRelease = true;
		}
	}

	// Override to hide the prompt when there's nothing to do here right now
	// (e.g. PipePatchStation while its target vent isn't broken).
	protected virtual bool IsAvailable() => true;

	// Override true for stations that can legitimately need solving more than
	// once — IsAvailable() is still consulted every time either way, this
	// only lifts the default one-shot lock that would otherwise mask it.
	protected virtual bool AllowRetrigger => false;

	// Override to pass setup data into the puzzle body via its own
	// configure(Dictionary) — see TaskPuzzleUI.gd. Puzzles that don't
	// implement configure just ignore whatever's returned here.
	protected virtual Godot.Collections.Dictionary BuildConfig() => new();

	// Called once the puzzle emits `solved`. Override to actually do
	// something (repair a vent, power a section, unlock a door).
	protected virtual void OnSolved()
	{
	}

	private void OpenPuzzle(CanvasLayer taskUi)
	{
		if (PuzzleScene == null)
		{
			GD.PushWarning($"TaskStationBase '{Name}': no PuzzleScene assigned.");
			return;
		}

		taskUi.Call(
			"open",
			PuzzleScene,
			Title,
			new Callable(this, MethodName.HandleSolved),
			_playerInRangeNode,
			BuildConfig()
		);
	}

	private void HandleSolved()
	{
		_solved = true;
		OnSolved();

		LevelStateManager levelState = GetNodeOrNull<LevelStateManager>("/root/LevelStateManager");
		string scenePath = GetTree().CurrentScene?.SceneFilePath;
		if (levelState != null && !string.IsNullOrEmpty(scenePath))
		{
			levelState.MarkStationSolved(scenePath, levelState.StableKeyFor(this));
		}
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not Sam sam) return;
		if (sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;
		_playerInRange = true;
		_playerInRangeNode = sam;
	}

	private void OnBodyExited(Node2D body)
	{
		if (body != _playerInRangeNode) return;
		_playerInRange = false;
		_playerInRangeNode = null;
	}
}
