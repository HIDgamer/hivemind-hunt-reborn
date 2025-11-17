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

	// Respawns the player at their checkpoint if one exists for the
	// current scene; otherwise falls back to a full scene reload, since
	// there's no known-safe position to place them at yet.
	public void RespawnPlayer()
	{
		SceneTree tree = GetTree();
		string currentScenePath = tree.CurrentScene.SceneFilePath;

		if (HasCheckpointForScene(currentScenePath))
		{
			foreach (Node node in tree.GetNodesInGroup("Player"))
			{
				if (node is Sam sam)
				{
					sam.RespawnAt(CheckpointPosition);
					return;
				}
			}
		}

		tree.ChangeSceneToFile(currentScenePath);
	}
}
