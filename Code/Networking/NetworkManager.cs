using Godot;

// Autoload. Owns the ENet peer connection itself (host or client) and the
// local player's chosen display name. Deliberately does not own player
// spawning — that's PlayerSpawner's job, kept separate so this class stays
// just "am I connected, and to what" bookkeeping.
public partial class NetworkManager : Node
{
	public const int DefaultPort = 8791;
	public const int MaxPlayers = 4;

	[Signal] public delegate void ServerCreatedEventHandler();
	[Signal] public delegate void ConnectionSucceededEventHandler();
	[Signal] public delegate void ConnectionFailedEventHandler();
	[Signal] public delegate void ServerDisconnectedEventHandler();

	public string LocalPlayerName { get; set; } = "PLAYER";
	// Shown by the Lobby on re-entry after a failed/lost session.
	public string LastError { get; set; } = "";
	// GDScript can't see a plain C# const via property access — this lets
	// Lobby.gd fall back to the same default the port field is meant to show.
	public int GetDefaultPort() => DefaultPort;
	// A fresh Godot process has a default OfflineMultiplayerPeer assigned —
	// NOT null — and it reports ConnectionStatus.Connected. Without filtering
	// it out, "is a session active" reads true on first launch before any
	// real host/join ever happened, which sent the first joining client of a
	// session down the SERVER path in PlayerSpawner (IsServer() is also true
	// offline): it spawned itself a solo world and never completed its join —
	// the "limbo server without the host" bug. Only after something called
	// Disconnect() (which nulls the peer) did later joins behave.
	public bool IsNetworked => Multiplayer.MultiplayerPeer != null
		&& Multiplayer.MultiplayerPeer is not OfflineMultiplayerPeer
		&& Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected;

	// Joining is a two-phase handshake with the scene load, not a single
	// call. The server fires its spawn packets the moment the ENet
	// connection is accepted — if the client is still in the Lobby (or
	// mid-fade) at that moment, its MultiplayerSpawner doesn't exist yet,
	// the packets land on nothing ("on_spawn_receive: spawner is null"),
	// and they are never re-sent: the client ends up in an empty world.
	// So the Lobby stores the target here, loads the level scene FIRST,
	// and PlayerSpawner completes the actual connection once the scene —
	// spawner included — is fully in the tree and ready to receive.
	public string PendingClientAddress { get; private set; } = "";
	public int PendingClientPort { get; private set; } = DefaultPort;
	public bool HasPendingJoin => !string.IsNullOrEmpty(PendingClientAddress);

	// THE way for gameplay code to ask "what role does this peer play".
	// Critically, a pending join counts as a client session: with the
	// deferred-join flow the level's nodes all run _Ready BEFORE the
	// connection opens, so any _Ready-time check against IsNetworked alone
	// reads false on a joining client — which made client lasers start
	// their own local cycle timers and client crates skip puppet-freezing,
	// desyncing both from the host no matter what the mirror RPCs said.
	public bool IsClientSession => HasPendingJoin || (IsNetworked && !Multiplayer.IsServer());
	public bool IsServerSession => !HasPendingJoin && IsNetworked && Multiplayer.IsServer();

	public void DeferJoin(string address, int port)
	{
		PendingClientAddress = address;
		PendingClientPort = port;
	}

	public Error CompletePendingJoin()
	{
		if (!HasPendingJoin) return Error.Unconfigured;
		string address = PendingClientAddress;
		PendingClientAddress = "";
		return StartClient(address, PendingClientPort);
	}

	public override void _Ready()
	{
		Multiplayer.ConnectedToServer += () => EmitSignal(SignalName.ConnectionSucceeded);
		Multiplayer.ConnectionFailed += OnConnectionFailed;
		Multiplayer.ServerDisconnected += OnServerDisconnected;
	}

	public Error StartHost(int port = DefaultPort)
	{
		var peer = new ENetMultiplayerPeer();
		Error err = peer.CreateServer(port, MaxPlayers);
		if (err != Error.Ok)
		{
			GD.PushWarning($"NetworkManager: failed to host on port {port}: {err}");
			return err;
		}

		Multiplayer.MultiplayerPeer = peer;
		EmitSignal(SignalName.ServerCreated);
		return Error.Ok;
	}

	public Error StartClient(string address, int port = DefaultPort)
	{
		var peer = new ENetMultiplayerPeer();
		Error err = peer.CreateClient(address, port);
		if (err != Error.Ok)
		{
			GD.PushWarning($"NetworkManager: failed to connect to {address}:{port}: {err}");
			return err;
		}

		Multiplayer.MultiplayerPeer = peer;
		return Error.Ok;
	}

	public void Disconnect()
	{
		PendingClientAddress = "";
		Multiplayer.MultiplayerPeer?.Close();
		Multiplayer.MultiplayerPeer = null;
	}

	// With the deferred-join flow the client is already sitting inside the
	// level scene when a connection fails or drops — without this it would
	// just stand alone in a dead world. Bounce back to the Lobby, which
	// shows LastError on entry.
	private void FailBackToLobby(string error)
	{
		LastError = error;
		Disconnect();
		GetTree().ChangeSceneToFile("res://Scenes/UI/Lobby.tscn");
	}

	private void OnConnectionFailed()
	{
		EmitSignal(SignalName.ConnectionFailed);
		FailBackToLobby("CONNECTION FAILED // CHECK ADDRESS + PORT");
	}

	private void OnServerDisconnected()
	{
		EmitSignal(SignalName.ServerDisconnected);
		FailBackToLobby("LINK LOST // SERVER CLOSED THE SESSION");
	}
}
