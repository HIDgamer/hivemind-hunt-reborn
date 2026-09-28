using Godot;

// Proximity door: opens automatically for anyone standing in range (unless
// RequireExternalPower gates it to a pressure plate/task station's
// Powered(bool) call instead), and closes itself again CloseDelay seconds
// after the last body leaves. Animation/networking engine lives in
// DoorBase — this class only owns "when should this specific door open."
public partial class Door : DoorBase
{
	[Export] public float CloseDelay = 3.0f;
	// When true, walking up to the door does nothing — it only opens while
	// externally powered (a pressure plate calling Powered(true)). This is
	// what lets a door act as an actual lock in a puzzle instead of a
	// courtesy door that slides open for anyone who approaches.
	[Export] public bool RequireExternalPower = false;

	private Area2D area;
	private int bodiesInArea = 0;
	private bool pressurePlateActive = false;
	private Timer closeTimer;

	protected override void OnDoorReady()
	{
		area = GetNode<Area2D>("Area2D");
		area.BodyEntered += OnBodyEntered;
		area.BodyExited += OnBodyExited;

		closeTimer = new Timer();
		AddChild(closeTimer);
		closeTimer.OneShot = true;
		closeTimer.Timeout += OnCloseTimerTimeout;

		// Start closed
		AnimatedSprite.Animation = "IdleClose";
		AnimatedSprite.Play();
		CollisionShape.Disabled = false;

		// Every peer used to run this exact same detection independently —
		// since a player's position is client-authoritative and only
		// replicated to everyone else with some latency, each peer's own
		// local copy of a shared door could reach a different Opening/
		// Closing/collision-disabled state at a given moment (one player
		// walks through what looks, on their own screen, like an open door
		// that's still solid on someone else's). Only the server (or a
		// plain singleplayer session, which is its own "server" here)
		// decides door state now; that decision is broadcast to every
		// client via RemoteTransition (see DoorBase), so every peer's
		// collision shape agrees with the same single source of truth.
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
	}

	private void OnBodyEntered(Node body)
	{
		if (body == this || !(body is CharacterBody2D)) return;
		// Clients no longer decide anything themselves — see OnDoorReady's
		// comment. Their own local Area2D still fires (needed so exiting
		// players work symmetrically below), but only the authoritative
		// machine acts on it.
		if (IsNetClient()) return;

		bodiesInArea++;
		if (RequireExternalPower && !pressurePlateActive) return;
		if (CurrentState == DoorState.Closed || CurrentState == DoorState.Closing)
		{
			StartOpening();
		}
		else if (CurrentState == DoorState.Open)
		{
			closeTimer.Stop();
		}
	}

	private void OnBodyExited(Node body)
	{
		if (body == this || !(body is CharacterBody2D)) return;
		if (IsNetClient()) return;

		bodiesInArea--;
		if (!ShouldStayOpen() && CurrentState == DoorState.Open)
		{
			closeTimer.Start(CloseDelay);
		}
	}

	protected override void OnOpened()
	{
		if (!IsNetClient() && !ShouldStayOpen())
		{
			closeTimer.Start(CloseDelay);
		}
	}

	private void OnCloseTimerTimeout()
	{
		if (!ShouldStayOpen() && CurrentState == DoorState.Open)
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
			if (CurrentState == DoorState.Closed || CurrentState == DoorState.Closing)
			{
				StartOpening();
			}
		}
		else if (!ShouldStayOpen() && CurrentState == DoorState.Open)
		{
			closeTimer.Start(CloseDelay);
		}
	}

	private bool ShouldStayOpen()
	{
		return bodiesInArea > 0 || pressurePlateActive;
	}
}
