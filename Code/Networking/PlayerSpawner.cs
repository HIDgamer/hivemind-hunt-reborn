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
	private MultiplayerSpawner _spawner;
	private bool _serverEventsSubscribed;

	public override void _Ready()
	{
		var networkManager = GetNode<NetworkManager>("/root/NetworkManager");
		// Three ways to arrive in a level:
		//  - hosting: peer already exists and we are the server
		//  - joining: NOT connected yet — the Lobby deliberately defers the
		//    actual connection until this scene (and the MultiplayerSpawner
		//    below) is in the tree, because the server fires its spawn
		//    packets the instant the connection lands and never re-sends
		//    them. Connecting from inside the Lobby made every spawn packet
		//    arrive before this node existed and vanish, which is exactly
		//    what "joining players never get a character" looked like.
		//  - plain single-player: neither — leave the pre-placed Sam alone.
		bool isServer = networkManager.IsNetworked && Multiplayer.IsServer();
		bool isJoiningClient = networkManager.HasPendingJoin;
		if (!isServer && !isJoiningClient)
		{
			return;
		}

		// The level ships with a single hardcoded Sam for the plain
		// single-player case — when actually networked, every player
		// (including the host) instead gets a spawned NetworkPlayer below,
		// so the pre-placed one has to go or the host would see double.
		GetTree().CurrentScene.GetNodeOrNull("Sam")?.QueueFree();

		_playersRoot = GetNode(PlayersRootPath);

		_spawner = GetNode<MultiplayerSpawner>("MultiplayerSpawner");
		_spawner.SpawnPath = _playersRoot.GetPath();
		// Custom spawn instead of AddSpawnableScene + manual AddChild: the
		// spawn data (peer id + position) is delivered verbatim to every
		// peer and applied inside SpawnFromData before the node enters the
		// tree. Relying on the synchronizer's spawn-state snapshot for the
		// position raced against the authority handoff — the joining
		// client's copies could instantiate at the scene default (0,0),
		// which is inside the tutorial's floor: the "spawns deep in the
		// ground" bug.
		_spawner.SpawnFunction = Callable.From<Variant, Node2D>(SpawnFromData);

		if (isServer)
		{
			Multiplayer.PeerConnected += SpawnPlayer;
			Multiplayer.PeerDisconnected += DespawnPlayer;
			_serverEventsSubscribed = true;
			SpawnPlayer(Multiplayer.GetUniqueId());
			// A fast client can complete its ENet handshake while the host
			// is still fading/loading this scene — PeerConnected has already
			// fired by the time we subscribed above, so sweep for peers that
			// are connected but have no player yet.
			foreach (int peerId in Multiplayer.GetPeers())
			{
				SpawnPlayer(peerId);
			}
		}
		else
		{
			// Scene + spawner are ready — NOW open the connection.
			networkManager.CompletePendingJoin();
		}
	}

	public override void _ExitTree()
	{
		// The MultiplayerApi outlives this scene; leaving stale handlers
		// subscribed would fire into a freed node (ObjectDisposedException)
		// the next time a session is hosted.
		if (_serverEventsSubscribed)
		{
			Multiplayer.PeerConnected -= SpawnPlayer;
			Multiplayer.PeerDisconnected -= DespawnPlayer;
			_serverEventsSubscribed = false;
		}
	}

	// Runs on EVERY peer (server calls Spawn below; clients run it when the
	// spawn packet arrives) with identical data — so name, position, and
	// authority are guaranteed to agree everywhere without any sync-timing
	// dependence.
	private Node2D SpawnFromData(Variant data)
	{
		var dict = data.AsGodotDictionary();
		long id = dict["id"].AsInt64();
		var player = NetworkPlayerScene.Instantiate<Node2D>();
		player.Name = id.ToString();
		player.Position = dict["pos"].AsVector2();
		player.SetMultiplayerAuthority((int)id);
		return player;
	}

	private void SpawnPlayer(long id)
	{
		if (NetworkPlayerScene == null) return;
		// Idempotent: the ready-sweep in _Ready and the PeerConnected signal
		// can both nominate the same peer.
		if (_playersRoot.GetNodeOrNull(id.ToString()) != null) return;

		// Stagger each arrival sideways so players don't materialize inside
		// each other on the same pad (they don't collide, but a perfect
		// overlap still reads as one person until someone moves).
		var data = new Godot.Collections.Dictionary
		{
			{ "id", id },
			{ "pos", SpawnPosition + new Vector2(28f * _playersRoot.GetChildCount(), 0f) },
		};
		_spawner.Spawn(data);
	}

	private void DespawnPlayer(long id)
	{
		Node existing = _playersRoot.GetNodeOrNull(id.ToString());
		existing?.QueueFree();
	}
}
