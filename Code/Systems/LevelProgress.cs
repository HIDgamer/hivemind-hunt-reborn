using Godot;
using System.Collections.Generic;

// Autoload. In-memory ledger of which levels this playthrough has
// completed — same "in-memory autoload SaveManager reads/writes through"
// shape as SquadAbilityState for abilities. LevelExitDoor calls
// CompleteLevel(scenePath) the moment the player exits through it; the
// level-select screen reads GetLevelOrder()/IsUnlocked() to decide what's
// selectable. All members are instance methods (never static fields) even
// where the underlying data is fixed — GDScript's LevelSelectMenu.gd needs
// to call these dynamically the same way it already does for every other
// C# autoload in this project, and static fields don't bind the same way.
public partial class LevelProgress : Node
{
	// Fixed authored order — level 0 is always unlocked. Beating the level
	// at index i unlocks index i+1. Not exported: level order is a design
	// decision, not save data.
	// Tutorial is deliberately NOT part of this chain — it's a standalone
	// practice run outside the numbered progression now that a dedicated
	// tutorial level exists (Level_00_Tutorial.tscn), reached via its own
	// menu button rather than the level-select screen.
	private static readonly string[] Order =
	{
		"res://Scenes/Maps/Level_01.tscn",
		"res://Scenes/Maps/Level_02.tscn",
	};

	private static readonly string[] DisplayNames =
	{
		"Level 1",
		"Level 2",
	};

	private readonly HashSet<string> _completedLevels = new();

	public string[] GetLevelOrder() => Order;

	public string GetDisplayName(string scenePath)
	{
		int index = System.Array.IndexOf(Order, scenePath);
		return index >= 0 ? DisplayNames[index] : scenePath;
	}

	public bool IsCompleted(string scenePath) => _completedLevels.Contains(scenePath);

	public bool IsUnlocked(string scenePath)
	{
		int index = System.Array.IndexOf(Order, scenePath);
		if (index <= 0) return true; // level 0, or anything outside the known order, is always open
		return IsCompleted(Order[index - 1]);
	}

	public void CompleteLevel(string scenePath)
	{
		_completedLevels.Add(scenePath);
	}

	public void Reset()
	{
		_completedLevels.Clear();
	}

	// For SaveManager to serialize/restore — a plain string[] rather than
	// the HashSet itself, since that's what marshals through ConfigFile/the
	// Variant boundary cleanly.
	public string[] GetCompletedLevels()
	{
		var arr = new string[_completedLevels.Count];
		_completedLevels.CopyTo(arr);
		return arr;
	}

	public void SetCompletedLevels(string[] scenePaths)
	{
		_completedLevels.Clear();
		if (scenePaths == null) return;
		foreach (string path in scenePaths) _completedLevels.Add(path);
	}
}
