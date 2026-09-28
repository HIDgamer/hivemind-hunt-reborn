using Godot;

// Single source of truth for where Sam appears in a level, networked or not.
// Server-authoritative in multiplayer: spawns a NetworkPlayer for every
// connected peer (including the host itself) and despawns it on disconnect.
// Plain single-player never touches any of that networking machinery, but
// still comes through here — levels no longer ship their own hardcoded Sam
// node (see SpawnSinglePlayer), so this is the only place a level's spawn
// position is authored at all. Sam's own _Ready() still overrides wherever
// this puts her with a checkpoint or exit-door arrival position when one
// applies (see CheckpointManager) — this is only the fallback for reaching
// a level with neither, i.e. a genuinely fresh arrival.
//
// Deliberately out of scope for this pass: reconnection/rejoin handling,
// spawn-point selection beyond a single fixed point, and mid-session host
// migration. This gets a host and their friends moving around together
// with name tags — not full session resilience.
public partial class PlayerSpawner : Node
{
	[Export] public NodePath PlayersRootPath = "../PlayersRoot";
	[Export] public PackedScene NetworkPlayerScene;
	// Single-player fallback — left unset on every level, since it always
	// resolves to the same plain, non-networked player scene regardless of
	// which level this is (see SpawnSinglePlayer). Exists as an export
	// mainly so a level could override it later if that ever changes.
	[Export] public PackedScene SinglePlayerScene;
	[Export] public Vector2 SpawnPosition = Vector2.Zero;

	private const string DefaultSinglePlayerScenePath = "res://Scenes/Characters/Sam.tscn";

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
		//  - plain single-player: neither — spawn the one local Sam here
		//    instead of falling through to the networked path below.
		bool isServer = networkManager.IsNetworked && Multiplayer.IsServer();
		bool isJoiningClient = networkManager.HasPendingJoin;

		_playersRoot = GetNode(PlayersRootPath);

		if (!isServer && !isJoiningClient)
		{
			SpawnSinglePlayer();
			return;
		}

		// The Tutorial level is the one remaining exception that still ships
		// a hardcoded "Sam" node (its scripted intro beats are authored
		// against that exact instance) — freeing it here is what stops the
		// host from seeing double when it's played networked. A no-op
		// everywhere else, since no other level has one to find any more.
		GetTree().CurrentScene.GetNodeOrNull("Sam")?.QueueFree();

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
			// are connected but have no player yet. Two overlapping sources
			// on purpose: NetworkManager's own PeerConnected subscription
			// (live from process start, so it can't miss a peer regardless
			// of how far along this scene's own loading was) plus the
			// standard GetPeers() sweep — SpawnPlayer is idempotent, so
			// nominating the same id from both costs nothing.
			foreach (long peerId in networkManager.DrainPendingSpawns())
			{
				SpawnPlayer(peerId);
			}
			foreach (int peerId in Multiplayer.GetPeers())
			{
				SpawnPlayer(peerId);
			}
		}
		else
		{
			// Scene + spawner are ready — NOW open the connection.
			networkManager.CompletePendingJoin();
			// Safety net: if the spawn packet is somehow still lost (the
			// scenario above should already prevent this on the host side,
			// but nothing guarantees delivery on a lossy real connection),
			// ask again rather than sitting in a silent, permanent softlock.
			StartSpawnWatchdog();
		}
	}

	// ── Client-side spawn retry ────────────────────────────────────────────
	private const float SpawnRetryInterval = 4f;
	private const int MaxSpawnRetries = 2;
	private int _spawnRetriesLeft = MaxSpawnRetries;

	private void StartSpawnWatchdog()
	{
		GetTree().CreateTimer(SpawnRetryInterval).Timeout += CheckSpawnedOrRetry;
	}

	private void CheckSpawnedOrRetry()
	{
		if (!IsInstanceValid(this)) return;
		if (_playersRoot.GetNodeOrNull(Multiplayer.GetUniqueId().ToString()) != null) return; // already spawned, nothing to do

		if (_spawnRetriesLeft <= 0)
		{
			var networkManager = GetNode<NetworkManager>("/root/NetworkManager");
			networkManager.LastError = "SPAWN FAILED // COULD NOT REACH HOST";
			networkManager.Disconnect();
			GetTree().ChangeSceneToFile("res://Scenes/UI/Lobby.tscn");
			return;
		}

		_spawnRetriesLeft--;
		RpcId(1, MethodName.RequestSpawn);
		StartSpawnWatchdog();
	}

	// Idempotent re-entry point into the exact same spawn logic the server
	// already uses — a client that still has no player node after the
	// normal join flow asks the server directly instead of waiting forever
	// for a packet that already went missing once.
	[Rpc(MultiplayerApi.RpcMode.AnyPeer)]
	private void RequestSpawn()
	{
		if (!Multiplayer.IsServer()) return;
		SpawnPlayer(Multiplayer.GetRemoteSenderId());
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

	// No MultiplayerSpawner/authority handoff needed here at all — this is
	// the one-and-only local Sam, not one of several peers' copies. Guarded
	// against a non-empty PlayersRoot so re-running _Ready() (shouldn't
	// normally happen, but see the same guard's reasoning in SpawnPlayer's
	// idempotency check above) can't ever produce a duplicate.
	private void SpawnSinglePlayer()
	{
		if (_playersRoot.GetChildCount() > 0) return;

		PackedScene scene = SinglePlayerScene ?? GD.Load<PackedScene>(DefaultSinglePlayerScenePath);
		if (scene == null)
		{
			GD.PushWarning("PlayerSpawner: no single-player scene available to spawn.");
			return;
		}

		var player = scene.Instantiate<Node2D>();
		player.Position = SpawnPosition;
		_playersRoot.AddChild(player);
	}
}
