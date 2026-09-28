using Godot;
using System.Collections.Generic;

// Autoload. Remembers per-level world-state facts that would otherwise
// reset every time a level scene reloads (dead enemies, solved puzzle
// stations, doors that changed state permanently) — same "in-memory
// ledger, SaveManager reads/writes through it" shape as SquadAbilityState/
// LevelProgress. Keyed by (scene path, the node's own path within that
// scene) rather than a manually-authored per-instance ID: a node's path
// relative to its level's root is already stable across reloads of the
// SAME scene file, so no extra per-instance setup is needed — see
// StableKeyFor.
public partial class LevelStateManager : Node
{
	private readonly Dictionary<string, HashSet<string>> _deadEnemies = new();
	private readonly Dictionary<string, HashSet<string>> _solvedStations = new();
	private readonly Dictionary<string, HashSet<string>> _triggeredDoors = new();

	// A node's path relative to its own level's root — e.g. "PlayersRoot/
	// Crusher" — stable across that same scene reloading since the level
	// layout doesn't change between visits, just its runtime state.
	public string StableKeyFor(Node node)
	{
		Node scene = node.GetTree()?.CurrentScene;
		if (scene == null) return node.Name;
		return scene.GetPathTo(node).ToString();
	}

	public void MarkEnemyDead(string scenePath, string nodeKey) => Mark(_deadEnemies, scenePath, nodeKey);
	public bool WasEnemyDead(string scenePath, string nodeKey) => Check(_deadEnemies, scenePath, nodeKey);

	public void MarkStationSolved(string scenePath, string nodeKey) => Mark(_solvedStations, scenePath, nodeKey);
	public bool WasStationSolved(string scenePath, string nodeKey) => Check(_solvedStations, scenePath, nodeKey);

	// Generic "this door's one-time transition already happened" — covers
	// both Door2 (opens permanently once) and Door3 (closes permanently
	// once); which default state each starts from otherwise is up to the
	// door itself, this ledger only remembers whether the flip occurred.
	public void MarkDoorTriggered(string scenePath, string nodeKey) => Mark(_triggeredDoors, scenePath, nodeKey);
	public bool WasDoorTriggered(string scenePath, string nodeKey) => Check(_triggeredDoors, scenePath, nodeKey);

	public void Reset()
	{
		_deadEnemies.Clear();
		_solvedStations.Clear();
		_triggeredDoors.Clear();
	}

	// For SaveManager — flattened "scenePath::nodeKey" entries rather than
	// the nested dictionaries themselves, since that's what marshals
	// through ConfigFile/the Variant boundary cleanly (same reasoning
	// LevelProgress.GetCompletedLevels() already follows).
	public string[] GetDeadEnemyEntries() => Flatten(_deadEnemies);
	public string[] GetSolvedStationEntries() => Flatten(_solvedStations);
	public string[] GetTriggeredDoorEntries() => Flatten(_triggeredDoors);

	public void SetDeadEnemyEntries(string[] entries) => Unflatten(_deadEnemies, entries);
	public void SetSolvedStationEntries(string[] entries) => Unflatten(_solvedStations, entries);
	public void SetTriggeredDoorEntries(string[] entries) => Unflatten(_triggeredDoors, entries);

	private static void Mark(Dictionary<string, HashSet<string>> table, string scenePath, string nodeKey)
	{
		if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(nodeKey)) return;
		if (!table.TryGetValue(scenePath, out HashSet<string> set))
		{
			set = new HashSet<string>();
			table[scenePath] = set;
		}
		set.Add(nodeKey);
	}

	private static bool Check(Dictionary<string, HashSet<string>> table, string scenePath, string nodeKey)
	{
		return table.TryGetValue(scenePath, out HashSet<string> set) && set.Contains(nodeKey);
	}

	private static string[] Flatten(Dictionary<string, HashSet<string>> table)
	{
		var entries = new List<string>();
		foreach (var pair in table)
		{
			foreach (string nodeKey in pair.Value)
			{
				entries.Add($"{pair.Key}::{nodeKey}");
			}
		}
		return entries.ToArray();
	}

	private static void Unflatten(Dictionary<string, HashSet<string>> table, string[] entries)
	{
		table.Clear();
		if (entries == null) return;
		foreach (string entry in entries)
		{
			int separator = entry.IndexOf("::");
			if (separator < 0) continue;
			string scenePath = entry[..separator];
			string nodeKey = entry[(separator + 2)..];
			Mark(table, scenePath, nodeKey);
		}
	}
}
