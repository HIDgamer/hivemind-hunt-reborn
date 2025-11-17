using Godot;

// Server-authoritative: spawns a NetworkPlayer for every connected peer
// (including the host itself) and despawns it on disconnect. A plain
// single-player load of this level never touches multiplayer at all — this
// entire node is a no-op unless NetworkManager reports an active connection.
//
// Deliberately out of scope for this pass: reconnection/rejoin handling,
// spawn-point selection beyond a single fixed point, and mid-session host
// migration. This gets a host and their friends moving around together
// with name tags — not full session resilience.
public partial class PlayerSpawner : Node
{
	[Export] public NodePath PlayersRootPath = "../PlayersRoot";
	[Export] public PackedScene NetworkPlayerScene;
	[Export] public Vector2 SpawnPosition = Vector2.Zero;

	private Node _playersRoot;

	public override void _Ready()
	{
		var networkManager = GetNode<NetworkManager>("/root/NetworkManager");
		if (!networkManager.IsNetworked)
		{
			return;
		}

		// The level ships with a single hardcoded Sam for the plain
		// single-player case — when actually networked, every player
		// (including the host) instead gets a spawned NetworkPlayer below,
		// so the pre-placed one has to go or the host would see double.
		GetTree().CurrentScene.GetNodeOrNull("Sam")?.QueueFree();

		_playersRoot = GetNode(PlayersRootPath);

		var spawner = GetNode<MultiplayerSpawner>("MultiplayerSpawner");
		spawner.SpawnPath = _playersRoot.GetPath();
		if (NetworkPlayerScene != null)
		{
			spawner.AddSpawnableScene(NetworkPlayerScene.ResourcePath);
		}

		if (Multiplayer.IsServer())
		{
			Multiplayer.PeerConnected += SpawnPlayer;
			Multiplayer.PeerDisconnected += DespawnPlayer;
			SpawnPlayer(Multiplayer.GetUniqueId());
		}
	}

	private void SpawnPlayer(long id)
	{
		if (NetworkPlayerScene == null) return;

		var player = NetworkPlayerScene.Instantiate<Node2D>();
		player.Name = id.ToString();
		player.GlobalPosition = SpawnPosition;
		player.SetMultiplayerAuthority((int)id);
		_playersRoot.AddChild(player, forceReadableName: true);
	}

	private void DespawnPlayer(long id)
	{
		Node existing = _playersRoot.GetNodeOrNull(id.ToString());
		existing?.QueueFree();
	}
}
