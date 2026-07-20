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

        // Check for bodies already in area (only CharacterBody2D)
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

    private void OnBodyEntered(Node body)
    {
        if (body == this || !(body is CharacterBody2D)) return;
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
        bodiesInArea--;
        if (!ShouldStayOpen() && currentState == State.Open)
        {
            closeTimer.Start(CloseDelay);
        }
    }

    private void StartOpening()
    {
        currentState = State.Opening;
        animatedSprite.Animation = "Open";
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
            if (!ShouldStayOpen())
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

    private void StartClosing()
    {
        currentState = State.Closing;
        animatedSprite.Animation = "Close";
        animatedSprite.Play();
        collisionShape.Disabled = false;
        PlayDoorSound();
    }

    // Generic "receive powered state" contract — the same method name
    // PressurePlateComponent's TargetNodePath calls on anything with a
    // Powered(bool) method, so a door is just one of possibly several
    // things a pressure plate can drive, not a special case.
    public void Powered(bool active)
    {
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
