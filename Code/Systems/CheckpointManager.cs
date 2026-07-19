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

	public void SetCheckpoint(Vector2 position, string scenePath)
	{
		HasCheckpoint = true;
		CheckpointPosition = position;
		CheckpointScenePath = scenePath;
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
