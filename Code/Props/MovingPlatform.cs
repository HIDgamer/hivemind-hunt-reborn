using Godot;
using System.Collections.Generic;

// Patrols between child Marker2D waypoints, in the order they appear in the
// scene tree, starting from and returning to its own spawn position.
// Riders are carried automatically — Godot's CharacterBody2D already
// propagates a moving platform's velocity to anything standing on it via
// move_and_slide(), so nothing extra is needed on the player's side.
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

		if (distance <= step)
		{
			GlobalPosition = target;
			Velocity = Vector2.Zero;
			_pauseTimer = PauseDuration;
			AdvanceWaypoint();
		}
		else
		{
			Velocity = toTarget.Normalized() * Speed;
		}

		MoveAndSlide();
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
