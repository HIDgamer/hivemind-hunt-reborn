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
	public bool IsNetworked => Multiplayer.MultiplayerPeer != null
		&& Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected;

	public override void _Ready()
	{
		Multiplayer.ConnectedToServer += () => EmitSignal(SignalName.ConnectionSucceeded);
		Multiplayer.ConnectionFailed += () => EmitSignal(SignalName.ConnectionFailed);
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
		Multiplayer.MultiplayerPeer?.Close();
		Multiplayer.MultiplayerPeer = null;
	}

	private void OnServerDisconnected()
	{
		Disconnect();
		EmitSignal(SignalName.ServerDisconnected);
	}
}
