using Godot;

public partial class Door : StaticBody2D
{
    [Export] public float CloseDelay = 3.0f;
    // When true, walking up to the door does nothing — it only opens while
    // externally powered (a pressure plate calling Powered(true)). This is
    // what lets a door act as an actual lock in a puzzle instead of a
    // courtesy door that slides open for anyone who approaches.
    [Export] public bool RequireExternalPower = false;

    private AnimatedSprite2D animatedSprite;
    private AudioStreamPlayer2D audioPlayer;
    private Area2D area;
    private CollisionShape2D collisionShape;
    private int bodiesInArea = 0;
    private bool pressurePlateActive = false;
    private Timer closeTimer;
    private enum State { Closed, Opening, Open, Closing }
    private State currentState = State.Closed;
    private bool _snapshotSubscribed;

    // Fired once the door actually settles into Open/Closed (not on every
    // Opening/Closing transition) — lets external systems (Narrator.gd)
    // react to a door resolving without polling the private state machine.
    [Signal] public delegate void OpenedEventHandler();
    [Signal] public delegate void ClosedEventHandler();

    public override void _Ready()
    {
        animatedSprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
        area = GetNode<Area2D>("Area2D");
        collisionShape = GetNode<CollisionShape2D>("CollisionShape2D");
        audioPlayer = GetNode<AudioStreamPlayer2D>("AudioStreamPlayer2D");

        closeTimer = new Timer();
        AddChild(closeTimer);
        closeTimer.OneShot = true;
        closeTimer.Timeout += OnCloseTimerTimeout;

        area.BodyEntered += OnBodyEntered;
        area.BodyExited += OnBodyExited;
        animatedSprite.AnimationFinished += OnAnimationFinished;

        // Start with closed
        animatedSprite.Animation = "IdleClose";
        animatedSprite.Play();
        collisionShape.Disabled = false;

        // Every peer used to run this exact same detection independently —
        // since a player's position is client-authoritative and only
        // replicated to everyone else with some latency, each peer's own
        // local copy of a shared door could reach a different Opening/
        // Closing/collision-disabled state at a given moment (one player
        // walks through what looks, on their own screen, like an open door
        // that's still solid on someone else's). Only the server (or a
        // plain singleplayer session, which is its own "server" here)
        // decides door state now; that decision is broadcast to every
        // client via RemoteTransition below, so every peer's collision
        // shape agrees with the same single source of truth.
        if (!IsNetClient())
        {
            var overlappingBodies = area.GetOverlappingBodies();
            foreach (var body in overlappingBodies)
            {
                if (body is CharacterBody2D && body != this)
                {
                    bodiesInArea++;
                }
            }
            if (ShouldStayOpen())
            {
                StartOpening();
            }
        }

        // Late joiners shouldn't have to wait for the next open/close edge
        // to agree with the server on what's already a solid wall or an
        // open doorway — hand each newly connected peer the current state
        // immediately (same pattern as Laser.cs's OnPeerConnectedSnapshot).
        if (IsNetServer())
        {
            Multiplayer.PeerConnected += OnPeerConnectedSnapshot;
            _snapshotSubscribed = true;
        }
    }

    public override void _ExitTree()
    {
        if (_snapshotSubscribed)
        {
            Multiplayer.PeerConnected -= OnPeerConnectedSnapshot;
            _snapshotSubscribed = false;
        }
    }

    private void OnPeerConnectedSnapshot(long peerId)
    {
        RpcId(peerId, MethodName.RemoteSnapshot, (int)currentState);
    }

    // A joining client may connect mid-open or mid-close — snap straight to
    // the settled Open/Closed look rather than replaying the transition
    // animation from the start, since there's nothing to "catch up" on
    // visually that matters as much as the collision state being correct
    // immediately.
    [Rpc(MultiplayerApi.RpcMode.Authority)]
    private void RemoteSnapshot(int state)
    {
        currentState = (State)state;
        bool open = currentState == State.Opening || currentState == State.Open;
        animatedSprite.Animation = open ? "IdleOpen" : "IdleClose";
        animatedSprite.Play();
        collisionShape.Disabled = open;
    }

    private bool IsNetClient()
    {
        var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
        return networkManager != null && networkManager.IsClientSession;
    }

    private bool IsNetServer()
    {
        var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
        return networkManager != null && networkManager.IsServerSession;
    }

    private void OnBodyEntered(Node body)
    {
        if (body == this || !(body is CharacterBody2D)) return;
        // Clients no longer decide anything themselves — see the _Ready
        // comment. Their own local Area2D still fires (needed so exiting
        // players work symmetrically below), but only the authoritative
        // machine acts on it.
        if (IsNetClient()) return;

        bodiesInArea++;
        if (RequireExternalPower && !pressurePlateActive) return;
        if (currentState == State.Closed || currentState == State.Closing)
        {
            StartOpening();
        }
        else if (currentState == State.Open)
        {
            closeTimer.Stop();
        }
    }

    private void OnBodyExited(Node body)
    {
        if (body == this || !(body is CharacterBody2D)) return;
        if (IsNetClient()) return;

        bodiesInArea--;
        if (!ShouldStayOpen() && currentState == State.Open)
        {
            closeTimer.Start(CloseDelay);
        }
    }

    private void StartOpening()
    {
        currentState = State.Opening;
        if (IsNetServer()) Rpc(MethodName.RemoteTransition, true);
        ApplyOpeningVisual();
    }

    private void StartClosing()
    {
        currentState = State.Closing;
        if (IsNetServer()) Rpc(MethodName.RemoteTransition, false);
        ApplyClosingVisual();
    }

    // Only the Opening/Closing *edges* travel over the network — same idea
    // as Laser.cs's Warning->Active step: once every peer starts the same
    // animation at roughly the same time, each peer's own AnimationFinished
    // deterministically settles into Open/Closed on its own, so that half
    // doesn't need its own round trip.
    [Rpc(MultiplayerApi.RpcMode.Authority)]
    private void RemoteTransition(bool opening)
    {
        if (opening)
        {
            currentState = State.Opening;
            ApplyOpeningVisual();
        }
        else
        {
            currentState = State.Closing;
            ApplyClosingVisual();
        }
    }

    private void ApplyOpeningVisual()
    {
        animatedSprite.Animation = "Open";
        animatedSprite.Play();
        PlayDoorSound();
    }

    private void ApplyClosingVisual()
    {
        animatedSprite.Animation = "Close";
        animatedSprite.Play();
        PlayDoorSound();
    }

    private void OnAnimationFinished()
    {
        if (currentState == State.Opening)
        {
            currentState = State.Open;
            animatedSprite.Animation = "IdleOpen";
            animatedSprite.Play();
            collisionShape.Disabled = true;
            if (!IsNetClient() && !ShouldStayOpen())
            {
                closeTimer.Start(CloseDelay);
            }
            EmitSignal(SignalName.Opened);
        }
        else if (currentState == State.Closing)
        {
            currentState = State.Closed;
            animatedSprite.Animation = "IdleClose";
            animatedSprite.Play();
            collisionShape.Disabled = false;
            EmitSignal(SignalName.Closed);
        }
    }

    private void OnCloseTimerTimeout()
    {
        if (!ShouldStayOpen() && currentState == State.Open)
        {
            StartClosing();
        }
    }

    // Generic "receive powered state" contract — the same method name
    // PressurePlateComponent's TargetNodePath calls on anything with a
    // Powered(bool) method, so a door is just one of possibly several
    // things a pressure plate can drive, not a special case.
    public void Powered(bool active)
    {
        if (IsNetClient()) return;

        pressurePlateActive = active;

        if (pressurePlateActive)
        {
            closeTimer.Stop();
            if (currentState == State.Closed || currentState == State.Closing)
            {
                StartOpening();
            }
        }
        else if (!ShouldStayOpen() && currentState == State.Open)
        {
            closeTimer.Start(CloseDelay);
        }
    }

    private bool ShouldStayOpen()
    {
        return bodiesInArea > 0 || pressurePlateActive;
    }

    private void PlayDoorSound()
    {
        if (audioPlayer.Stream == null) return;
        audioPlayer.Play();
    }

}
