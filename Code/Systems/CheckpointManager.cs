using Godot;

// Autoload singleton (see project.godot [autoload]) tracking the player's
// most recently touched checkpoint. An autoload lives outside the scene
// tree that change_scene_to_file() swaps out, so it survives a scene
// reload — letting death or falling into a boundary volume respawn the
// player at their last checkpoint instead of restarting the whole level
// from scratch.
public partial class CheckpointManager : Node
{
	public bool HasCheckpoint { get; private set; } = false;
	public Vector2 CheckpointPosition { get; private set; } = Vector2.Zero;
	public string CheckpointScenePath { get; private set; } = "";

	// Set by SaveManager.LoadSlot right before changing scene, so the level
	// being loaded knows to reposition Sam at the saved checkpoint instead of
	// her scene-authored default spawn — see Sam._Ready's ConsumePendingRespawn
	// call, single-player only.
	public bool PendingRespawnOnLoad { get; private set; } = false;

	public void SetCheckpoint(Vector2 position, string scenePath)
	{
		bool isNewCheckpoint = !HasCheckpoint || scenePath != CheckpointScenePath || position != CheckpointPosition;
		HasCheckpoint = true;
		CheckpointPosition = position;
		CheckpointScenePath = scenePath;

		if (isNewCheckpoint)
		{
			GetNodeOrNull<SaveManager>("/root/SaveManager")?.Autosave(scenePath, position);
		}
	}

	// Called by SaveManager right before a scene change so the destination
	// level knows where to place Sam once it's ready.
	public void RequestRespawnOnLoad(Vector2 position, string scenePath)
	{
		HasCheckpoint = true;
		CheckpointPosition = position;
		CheckpointScenePath = scenePath;
		PendingRespawnOnLoad = true;
	}

	// One-shot: only fires the first time the freshly loaded scene asks, so a
	// later in-scene respawn (death, boundary fall) doesn't get short-circuited
	// by a stale pending flag.
	public bool ConsumePendingRespawn(string currentScenePath, out Vector2 position)
	{
		position = CheckpointPosition;
		if (PendingRespawnOnLoad && CheckpointScenePath == currentScenePath)
		{
			PendingRespawnOnLoad = false;
			return true;
		}
		return false;
	}

	// GDScript-callable (no `out` param, which doesn't marshal across the
	// scripting bridge) — used by New Game to make sure a checkpoint left
	// over from a previous session, or from browsing the Load screen, never
	// leaks into a fresh run.
	public void ClearPendingRespawn()
	{
		PendingRespawnOnLoad = false;
	}

	// A checkpoint from a different level shouldn't apply here.
	public bool HasCheckpointForScene(string scenePath)
	{
		return HasCheckpoint && CheckpointScenePath == scenePath;
	}

	// Respawns the LOCAL player at their checkpoint if one exists for the
	// current scene. Single-player may fall back to a full scene reload
	// when no checkpoint was reached yet; a networked peer NEVER may — a
	// reload tears down the shared world (players, spawner, synchronizers)
	// on one peer only, which desyncs and effectively kills the session for
	// everyone (observed as "the peer disconnected when the host died").
	public void RespawnPlayer()
	{
		SceneTree tree = GetTree();
		string currentScenePath = tree.CurrentScene.SceneFilePath;
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		bool networked = networkManager != null && networkManager.IsNetworked;

		Sam localPlayer = null;
		foreach (Node node in tree.GetNodesInGroup("Player"))
		{
			// In multiplayer the group also contains other players' puppets —
			// only the copy this machine has authority over is "the player".
			if (node is Sam sam && (!networked || sam.IsMultiplayerAuthority()))
			{
				localPlayer = sam;
				break;
			}
		}

		if (localPlayer != null && HasCheckpointForScene(currentScenePath))
		{
			localPlayer.RespawnAt(CheckpointPosition);
			return;
		}

		if (networked)
		{
			// No checkpoint yet: back to the level's spawn pad instead of a
			// scene reload.
			Vector2 spawnPoint = Vector2.Zero;
			if (tree.CurrentScene.GetNodeOrNull("PlayerSpawner") is PlayerSpawner spawner)
			{
				spawnPoint = spawner.SpawnPosition;
			}
			localPlayer?.RespawnAt(spawnPoint);
			return;
		}

		tree.ChangeSceneToFile(currentScenePath);
	}
}
