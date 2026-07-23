using Godot;

// A single laser blaster shot fired by LaserTurret — a short, fast, bright
// streak that travels in a straight line and pops on the first thing it
// hits, instead of Laser.cs's continuous beam. Deliberately closer to a
// Star Wars blaster bolt than a sci-fi cutting beam: one hit, one modest
// chunk of damage, then it's gone — the danger comes from the turret's
// aim/timing, not from standing in a damage-per-tick death ray.
//
// Known multiplayer gap, same as AcidSpit.gd: only ever spawned by
// LaserTurret's own server-only AI, so this only ever exists in the
// server's scene tree — invisible on clients until turret AI gets a proper
// MultiplayerSpawner treatment. Out of scope for this pass.
public partial class LaserBolt : Area2D
{
	[Export] public float Speed = 820f;
	[Export] public int Damage = 1;
	[Export] public float Lifetime = 1.6f;

	private Vector2 _direction = Vector2.Right;
	private float _lifeTimer;
	private bool _spent;

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
	}

	public void Launch(Vector2 fromPosition, Vector2 direction)
	{
		GlobalPosition = fromPosition;
		_direction = direction.Normalized();
		Rotation = _direction.Angle();
		_lifeTimer = Lifetime;
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_spent) return;

		GlobalPosition += _direction * Speed * (float)delta;
		_lifeTimer -= (float)delta;
		if (_lifeTimer <= 0f)
		{
			QueueFree();
		}
	}

	private void OnBodyEntered(Node2D body)
	{
		if (_spent) return;

		if (body.IsInGroup("Player"))
		{
			HealthComponent health = body.GetNodeOrNull<HealthComponent>("HealthComponent");
			health?.Damage(Damage, _direction);
		}

		Pop();
	}

	// World geometry and the player both end the bolt on contact — a laser
	// blast doesn't punch through walls any more than it punches through
	// the person it hit.
	private void Pop()
	{
		_spent = true;
		QueueFree();
	}
}
