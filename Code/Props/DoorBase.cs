using Godot;

// Shared "reversible sprite-sheet open/close" engine for every door variant
// (Door.cs's proximity door, Door2's multi-puzzle gate, Door3's one-way trap
// door). Owns the AnimatedSprite2D Open/Close/IdleOpen/IdleClose state
// machine, the door sound, and the networking shape Door.cs originally
// proved out: only the authority decides to open/close, the Opening/Closing
// *edge* broadcasts once over RPC, and every peer's own AnimationFinished
// deterministically settles into Open/Closed on its own (so the settle half
// doesn't need its own round trip) — plus a late-joiner snapshot so a
// client connecting mid-transition doesn't have to guess. Subclasses only
// ever decide WHEN to call StartOpening()/StartClosing() and what should
// happen once a transition actually settles (OnOpened/OnClosed) — never how
// the animation/network plumbing itself works.
public abstract partial class DoorBase : StaticBody2D
{
	protected AnimatedSprite2D AnimatedSprite;
	protected AudioStreamPlayer2D AudioPlayer;
	protected CollisionShape2D CollisionShape;

	protected enum DoorState { Closed, Opening, Open, Closing }
	protected DoorState CurrentState = DoorState.Closed;
	private bool _snapshotSubscribed;

	// Fired once the door actually settles into Open/Closed (not on every
	// Opening/Closing transition) — lets external systems (Narrator.gd)
	// react to a door resolving without polling the private state machine.
	[Signal] public delegate void OpenedEventHandler();
	[Signal] public delegate void ClosedEventHandler();

	public override void _Ready()
	{
		AnimatedSprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		CollisionShape = GetNode<CollisionShape2D>("CollisionShape2D");
		AudioPlayer = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");

		AnimatedSprite.AnimationFinished += OnAnimationFinished;

		// Late joiners shouldn't have to wait for the next open/close edge
		// to agree with the server on what's already a solid wall or an
		// open doorway — hand each newly connected peer the current state
		// immediately (same pattern as Laser.cs's OnPeerConnectedSnapshot).
		if (IsNetServer())
		{
			Multiplayer.PeerConnected += OnPeerConnectedSnapshot;
			_snapshotSubscribed = true;
		}

		OnDoorReady();
	}

	public override void _ExitTree()
	{
		if (_snapshotSubscribed)
		{
			Multiplayer.PeerConnected -= OnPeerConnectedSnapshot;
			_snapshotSubscribed = false;
		}
	}

	// Override for subclass-specific setup — initial open/closed pose,
	// detection Area2D wiring, etc. Runs after AnimatedSprite/CollisionShape/
	// AudioPlayer above are already resolved.
	protected virtual void OnDoorReady() { }

	// Reloading the same level (e.g. backtracking through an exit door)
	// would otherwise forget a one-way door (Door2 permanently open, Door3
	// permanently sealed) ever changed state — LevelStateManager remembers
	// it per (scene, this node's own path) so a subclass's OnDoorReady can
	// start in the already-triggered pose instead of its normal default.
	protected bool WasTriggeredBefore()
	{
		LevelStateManager levelState = GetNodeOrNull<LevelStateManager>("/root/LevelStateManager");
		string scenePath = GetTree().CurrentScene?.SceneFilePath;
		if (levelState == null || string.IsNullOrEmpty(scenePath)) return false;
		return levelState.WasDoorTriggered(scenePath, levelState.StableKeyFor(this));
	}

	protected void MarkTriggered()
	{
		LevelStateManager levelState = GetNodeOrNull<LevelStateManager>("/root/LevelStateManager");
		string scenePath = GetTree().CurrentScene?.SceneFilePath;
		if (levelState == null || string.IsNullOrEmpty(scenePath)) return;
		levelState.MarkDoorTriggered(scenePath, levelState.StableKeyFor(this));
	}

	protected bool IsNetClient()
	{
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		return networkManager != null && networkManager.IsClientSession;
	}

	protected bool IsNetServer()
	{
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		return networkManager != null && networkManager.IsServerSession;
	}

	private void OnPeerConnectedSnapshot(long peerId)
	{
		RpcId(peerId, MethodName.RemoteSnapshot, (int)CurrentState);
	}

	// A joining client may connect mid-open or mid-close — snap straight to
	// the settled Open/Closed look rather than replaying the transition
	// animation from the start, since there's nothing to "catch up" on
	// visually that matters as much as the collision state being correct
	// immediately.
	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteSnapshot(int state)
	{
		CurrentState = (DoorState)state;
		bool open = CurrentState == DoorState.Opening || CurrentState == DoorState.Open;
		AnimatedSprite.Animation = open ? "IdleOpen" : "IdleClose";
		AnimatedSprite.Play();
		CollisionShape.Disabled = open;
	}

	protected void StartOpening()
	{
		if (CurrentState == DoorState.Opening || CurrentState == DoorState.Open) return;
		CurrentState = DoorState.Opening;
		if (IsNetServer()) Rpc(MethodName.RemoteTransition, true);
		ApplyOpeningVisual();
	}

	protected void StartClosing()
	{
		if (CurrentState == DoorState.Closing || CurrentState == DoorState.Closed) return;
		CurrentState = DoorState.Closing;
		if (IsNetServer()) Rpc(MethodName.RemoteTransition, false);
		ApplyClosingVisual();
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteTransition(bool opening)
	{
		if (opening)
		{
			CurrentState = DoorState.Opening;
			ApplyOpeningVisual();
		}
		else
		{
			CurrentState = DoorState.Closing;
			ApplyClosingVisual();
		}
	}

	private void ApplyOpeningVisual()
	{
		AnimatedSprite.Animation = "Open";
		AnimatedSprite.Play();
		PlayDoorSound();
	}

	private void ApplyClosingVisual()
	{
		AnimatedSprite.Animation = "Close";
		AnimatedSprite.Play();
		PlayDoorSound();
	}

	private void OnAnimationFinished()
	{
		if (CurrentState == DoorState.Opening)
		{
			CurrentState = DoorState.Open;
			AnimatedSprite.Animation = "IdleOpen";
			AnimatedSprite.Play();
			CollisionShape.Disabled = true;
			EmitSignal(SignalName.Opened);
			OnOpened();
		}
		else if (CurrentState == DoorState.Closing)
		{
			CurrentState = DoorState.Closed;
			AnimatedSprite.Animation = "IdleClose";
			AnimatedSprite.Play();
			CollisionShape.Disabled = false;
			EmitSignal(SignalName.Closed);
			OnClosed();
		}
	}

	// Overridable hooks for once a transition actually settles — e.g.
	// Door.cs starting its auto-close timer, Door3 locking itself out of
	// ever reopening. Default no-op; most subclasses only need one of them.
	protected virtual void OnOpened() { }
	protected virtual void OnClosed() { }

	private void PlayDoorSound()
	{
		if (AudioPlayer == null || AudioPlayer.Stream == null) return;
		AudioPlayer.Play();
	}
}
