using Godot;
using System.Collections.Generic;

// Patrols between child Marker2D waypoints, in the order they appear in the
// scene tree, starting from and returning to its own spawn position.
// Riders are NOT carried automatically — Rapier2D (this project's physics
// backend) doesn't reliably propagate a moving platform's velocity to
// whatever's standing on it via move_and_slide() (the same gap that made
// Sam.UpdatePlayerRiding a hand-rolled workaround for standing on another
// player). See Sam.UpdatePlatformRiding for the matching hand-rolled carry
// on the player's side — this component only has to move itself correctly.
// With no Marker2D children placed, it just sits still (a plain static
// platform), so the base scene is safe to instance without configuration.
public partial class MovingPlatform : CharacterBody2D
{
	[Export] public float Speed = 60f;
	// true: reverse direction at each end. false: loop from the last
	// waypoint straight back to the first (good for a circular path).
	[Export] public bool PingPong = true;
	[Export] public float PauseDuration = 0f;

	private readonly List<Vector2> _waypoints = new();
	private int _targetIndex = 1;
	private int _direction = 1;
	private float _pauseTimer = 0f;

	public override void _Ready()
	{
		_waypoints.Add(GlobalPosition);
		foreach (Node child in GetChildren())
		{
			if (child is Marker2D marker)
			{
				_waypoints.Add(marker.GlobalPosition);
			}
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		// Networked: only the authority (the host — this is static level
		// content, never spawned per-peer, so a node's multiplayer authority
		// already defaults to the server with no extra plumbing) actually
		// simulates the patrol. Every other peer used to run its own
		// independent copy of this same waypoint loop, relying purely on
		// cross-peer determinism (same fixed Speed/waypoints) to stay in
		// sync, with no correction if timing/physics ever drifted — a real
		// risk for anything riding it (see Sam.UpdatePlatformRiding). Now
		// clients just receive the authoritative `position` via the
		// MultiplayerSynchronizer on this scene instead of simulating their
		// own copy at all.
		if (Multiplayer.HasMultiplayerPeer() && !IsMultiplayerAuthority()) return;

		if (_waypoints.Count < 2)
		{
			Velocity = Vector2.Zero;
			MoveAndSlide();
			return;
		}

		if (_pauseTimer > 0f)
		{
			_pauseTimer -= (float)delta;
			Velocity = Vector2.Zero;
			MoveAndSlide();
			return;
		}

		Vector2 target = _waypoints[_targetIndex];
		Vector2 toTarget = target - GlobalPosition;
		float distance = toTarget.Length();
		float step = Speed * (float)delta;

		// Never assign GlobalPosition directly — arriving at a waypoint caps
		// Velocity to exactly the distance left this frame instead, so
		// MoveAndSlide() alone carries the platform the last short stretch.
		// A manual position write here would be a one-frame teleport outside
		// the physics-integrated motion, which is exactly the kind of jitter
		// a rider (see Sam.UpdatePlatformRiding, which reads this platform's
		// GlobalPosition delta every frame) would otherwise pick up as a pop.
		bool arriving = distance <= step;
		Velocity = arriving ? toTarget / (float)delta : toTarget.Normalized() * Speed;

		MoveAndSlide();

		if (arriving)
		{
			Velocity = Vector2.Zero;
			_pauseTimer = PauseDuration;
			AdvanceWaypoint();
		}
	}

	private void AdvanceWaypoint()
	{
		if (PingPong)
		{
			_targetIndex += _direction;
			if (_targetIndex >= _waypoints.Count)
			{
				_targetIndex = _waypoints.Count - 2;
				_direction = -1;
			}
			else if (_targetIndex < 0)
			{
				_targetIndex = 1;
				_direction = 1;
			}
		}
		else
		{
			_targetIndex = (_targetIndex + 1) % _waypoints.Count;
		}
	}
}
