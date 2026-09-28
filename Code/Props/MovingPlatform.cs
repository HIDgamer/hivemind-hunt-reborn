using Godot;
using System.Collections.Generic;

// Two patrol modes, auto-selected by whether any child Marker2D waypoints
// exist:
//   - Waypoint mode: seeks each Marker2D in turn, in scene-tree order,
//     starting from and returning to its own spawn position — precise,
//     hand-authored paths.
//   - Collision mode (no Marker2D children — this is what a "Platform"
//     spawned via ObjectManager's TileMap marker always gets, since that
//     spawn path has no way to attach child nodes to what it instances):
//     drives straight along PatrolDirection at Speed and reverses the
//     instant MoveAndSlide() reports a collision opposing that direction —
//     bounces between whatever floor/wall tiles it's sandwiched between,
//     no waypoints authored at all. Placing this scene directly in a level
//     and adding real Marker2D children still overrides this with whatever
//     custom path you want.
//
// Riders are carried automatically by Godot's own native moving-platform
// support (this project runs on GodotPhysics2D — see project.godot) once a
// rider's own CharacterBody2D.MoveAndSlide() detects it's standing on this
// body as its floor. Rapier2D (the previous backend) didn't reliably
// propagate that, which is why Sam used to need a hand-rolled carry
// (Sam.UpdatePlatformRiding, since removed) just for this — this component
// only ever had to move itself correctly either way.
public partial class MovingPlatform : CharacterBody2D
{
	[Export] public float Speed = 60f;
	// Waypoint mode only. true: reverse direction at each end. false: loop
	// from the last waypoint straight back to the first (good for a
	// circular path).
	[Export] public bool PingPong = true;
	[Export] public float PauseDuration = 0f;

	[ExportGroup("Collision Patrol")]
	// Used only when no Marker2D children exist — see class comment.
	[Export] public Vector2 PatrolDirection = Vector2.Right;

	private readonly List<Vector2> _waypoints = new();
	private int _targetIndex = 1;
	private int _direction = 1;
	private float _pauseTimer = 0f;
	private bool _useCollisionPatrol;

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

		_useCollisionPatrol = _waypoints.Count < 2;
	}

	public override void _PhysicsProcess(double delta)
	{
		// Networked: only the authority (the host — this is static level
		// content, never spawned per-peer, so a node's multiplayer authority
		// already defaults to the server with no extra plumbing) actually
		// simulates the patrol. Every other peer used to run its own
		// independent copy of this same movement, relying purely on
		// cross-peer determinism to stay in sync, with no correction if
		// timing/physics ever drifted — a real risk for anything riding it.
		// Now clients just receive the authoritative `position` via the
		// MultiplayerSynchronizer on this scene instead of simulating their
		// own copy at all.
		if (Multiplayer.HasMultiplayerPeer() && !IsMultiplayerAuthority()) return;

		float deltaTime = (float)delta;

		if (_pauseTimer > 0f)
		{
			_pauseTimer -= deltaTime;
			Velocity = Vector2.Zero;
			MoveAndSlide();
			return;
		}

		if (_useCollisionPatrol)
		{
			TickCollisionPatrol();
		}
		else
		{
			TickWaypointPatrol(deltaTime);
		}
	}

	// Drives straight in the current direction and flips it the moment a
	// collision's surface normal opposes travel — the platform just bounces
	// off whatever it runs into (floor, wall, ceiling), no waypoints needed.
	private void TickCollisionPatrol()
	{
		Vector2 dir = PatrolDirection.Normalized() * _direction;
		Velocity = dir * Speed;
		MoveAndSlide();

		for (int i = 0; i < GetSlideCollisionCount(); i++)
		{
			KinematicCollision2D collision = GetSlideCollision(i);
			if (collision.GetNormal().Dot(dir) < -0.1f)
			{
				_direction = -_direction;
				_pauseTimer = PauseDuration;
				break;
			}
		}
	}

	private void TickWaypointPatrol(float deltaTime)
	{
		Vector2 target = _waypoints[_targetIndex];
		Vector2 toTarget = target - GlobalPosition;
		float distance = toTarget.Length();
		float step = Speed * deltaTime;

		// Never assign GlobalPosition directly — arriving at a waypoint caps
		// Velocity to exactly the distance left this frame instead, so
		// MoveAndSlide() alone carries the platform the last short stretch.
		// A manual position write here would be a one-frame teleport outside
		// the physics-integrated motion, which is exactly the kind of jitter
		// a rider would otherwise pick up as a pop, since native platform
		// carry only sees motion that comes through MoveAndSlide().
		bool arriving = distance <= step;
		Velocity = arriving ? toTarget / deltaTime : toTarget.Normalized() * Speed;

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
