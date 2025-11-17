using Godot;

public partial class door : StaticBody2D
{
    [Export] public float CloseDelay = 3.0f;

    private AnimatedSprite2D animatedSprite;
    private AudioStreamPlayer2D audioPlayer;
    private Area2D area;
    private CollisionShape2D collisionShape;
    private int bodiesInArea = 0;
    private Timer closeTimer;
    private enum State { Closed, Opening, Open, Closing }
    private State currentState = State.Closed;

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
        if (bodiesInArea > 0)
        {
            StartOpening();
        }

    }

    private void OnBodyEntered(Node body)
    {
        if (body == this || !(body is CharacterBody2D)) return;
        bodiesInArea++;
        if (currentState == State.Closed || currentState == State.Closing)
        {
            StartOpening();
        }
        else if (currentState == State.Open)
        {
            // Restart timer
            closeTimer.Stop();
            closeTimer.Start(CloseDelay);
        }
    }

    private void OnBodyExited(Node body)
    {
        if (body == this || !(body is CharacterBody2D)) return;
        bodiesInArea--;
        if (bodiesInArea <= 0 && currentState == State.Open)
        {
            closeTimer.Start(CloseDelay);
        }
    }

    private void StartOpening()
    {
        currentState = State.Opening;
        animatedSprite.Animation = "Open";
        animatedSprite.Play();
        audioPlayer.Stream = GD.Load<AudioStream>("res://Sound/machines/airlock.ogg");
        audioPlayer.Play();
    }

    private void OnAnimationFinished()
    {
        if (currentState == State.Opening)
        {
            currentState = State.Open;
            animatedSprite.Animation = "IdleOpen";
            animatedSprite.Play();
            collisionShape.Disabled = true;
            if (bodiesInArea > 0)
            {
                closeTimer.Start(CloseDelay);
            }
        }
        else if (currentState == State.Closing)
        {
            currentState = State.Closed;
            animatedSprite.Animation = "IdleClose";
            animatedSprite.Play();
            collisionShape.Disabled = false;
        }
    }

    private void OnCloseTimerTimeout()
    {
        if (bodiesInArea <= 0 && currentState == State.Open)
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
        audioPlayer.Stream = GD.Load<AudioStream>("res://Sound/machines/airlock.ogg");
        audioPlayer.Play();
    }

}