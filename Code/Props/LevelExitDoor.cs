using Godot;

// A level-exit trigger — walking into it transitions to a different scene.
// Unlike Door.cs (a StaticBody2D puzzle prop that opens/closes on power but
// never changes scenes), this is purely a scene-to-scene transition point:
// no unlock/power requirement yet, just an Area2D the player walks through.
// Same "detection Area2D, layer=0/mask=2, body is Sam" convention already
// established by Checkpoint.cs/NpcDialogueTrigger.cs.
//
// Reusable, not one-shot — levels are meant to be walked back and forth
// between (Tutorial's exit door leads to Level_01's, whose own exit door
// leads right back). A short cooldown after each use replaces what used to
// be a permanent trigger lock, purely to stop the door firing again the
// instant a fresh scene load drops the player back down still overlapping
// an entrance right next to it — not to stop legitimate repeat use.
//
// TargetLevelPath is a plain string (a uid:// or res:// path), deliberately
// NOT a PackedScene export. Two levels whose exit doors point at each other
// (Tutorial -> Level_01 -> Tutorial) would otherwise embed a hard
// PackedScene ext_resource dependency in both directions — a genuine
// circular reference .tscn's format can't resolve, which surfaced as
// "[ext_resource] referenced non-existent resource" parse errors and
// TargetLevel silently coming back null at runtime. A string is just data,
// not a resource Godot has to preload, so the cycle can't happen — same
// reasoning MainMenu.gd's own TUTORIAL_LEVEL constant and LevelProgress's
// level order already follow.
//
// ArrivalPosition is where Sam appears in the TARGET level — without it,
// every trip through any exit door dumps the player back at that level's
// single hardcoded default spawn, which is what made backtracking feel like
// being sent to the start over again. Point it at wherever the matching
// entrance/door sits in the destination level.
//
// Also marks the level being LEFT as completed in LevelProgress — this is
// the only place that happens, so any level with an exit door genuinely
// unlocks the next one in LevelProgress's authored order the moment a
// player reaches it, with no separate "level complete" trigger needed.
public partial class LevelExitDoor : Area2D
{
	[Export] public string TargetLevelPath = "";
	[Export] public Vector2 ArrivalPosition;

	private const float RetriggerCooldown = 1f;
	private float _cooldownRemaining;

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
	}

	public override void _Process(double delta)
	{
		if (_cooldownRemaining > 0f) _cooldownRemaining -= (float)delta;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (_cooldownRemaining > 0f) return;
		if (body is not Sam sam) return;
		// Only the locally-authoritative Sam should fire this — otherwise a
		// remote player's replicated position would also trigger the
		// transition on every other peer's own machine.
		if (sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;

		if (string.IsNullOrEmpty(TargetLevelPath))
		{
			GD.PushWarning("LevelExitDoor: TargetLevelPath not set — nothing to transition to.");
			return;
		}

		_cooldownRemaining = RetriggerCooldown;

		string currentScenePath = GetTree().CurrentScene?.SceneFilePath;
		if (!string.IsNullOrEmpty(currentScenePath))
		{
			GetNodeOrNull<LevelProgress>("/root/LevelProgress")?.CompleteLevel(currentScenePath);
		}

		// Sam's own arrival check compares against SceneFilePath, which is
		// always a resolved res:// path — resolving a uid:// TargetLevelPath
		// here too is what actually makes the comparison match. Without this,
		// RequestArrivalOnLoad silently stored an arrival keyed to a string
		// that could never equal SceneFilePath, so it was never consumed.
		string resolvedTargetPath = ResolveScenePath(TargetLevelPath);
		GetNodeOrNull<CheckpointManager>("/root/CheckpointManager")?.RequestArrivalOnLoad(ArrivalPosition, resolvedTargetPath);

		CanvasLayer sceneTransition = GetNodeOrNull<CanvasLayer>("/root/SceneTransition");
		if (sceneTransition != null)
		{
			sceneTransition.Call("change_scene", TargetLevelPath);
		}
		else
		{
			GetTree().ChangeSceneToFile(TargetLevelPath);
		}
	}

	private static string ResolveScenePath(string path)
	{
		if (!path.StartsWith("uid://")) return path;
		Resource resource = ResourceLoader.Load(path);
		return resource != null ? resource.ResourcePath : path;
	}
}
