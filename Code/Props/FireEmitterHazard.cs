using Godot;

// Real hazard version of the purely-decorative FireEmitter.tscn — this
// scene instances that same visual as a child (reuse, not a duplicate copy)
// and adds actual gameplay: touch damage (same overlap-cooldown-HealthComponent
// convention as SparkHazard.cs) plus continuous room heating via
// GasSimulation.AddHeat, so standing near it raises the room's temperature
// over time even with no gas involved at all.
public partial class FireEmitterHazard : Area2D
{
	// Deliberately low — SparkHazard.cs's own history is why: it used to
	// deal 5 damage against a 3 HP player, which meant the killing hit
	// never fired TookDamage at all (HealthComponent.Damage only emits that
	// on a non-lethal hit — a killing blow emits Died instead), so there
	// was no hurt flash/sound/knockback before dying, just "touched it,
	// dead." Low enough here that every tick plays normal hurt feedback.
	[Export] public int Damage = 1;
	[Export] public double DamageCooldown = 0.5;
	[Export] public float BurnDuration = 3f;

	[ExportGroup("Heat")]
	[Export] public float HeatPerSecond = 40f;
	[Export] public float HeatRadius = 64f;

	private double _timeSinceLastDamage = 999.0;

	public override void _Process(double delta)
	{
		GasSimulation.Instance?.AddHeat(GlobalPosition, HeatPerSecond * (float)delta, HeatRadius);

		_timeSinceLastDamage += delta;
		if (_timeSinceLastDamage < DamageCooldown) return;

		foreach (Node2D body in GetOverlappingBodies())
		{
			if (TryDamage(body))
			{
				_timeSinceLastDamage = 0.0;
				return;
			}
		}
	}

	private bool TryDamage(Node2D target)
	{
		HealthComponent health = target.GetNodeOrNull<HealthComponent>("HealthComponent");
		if (health == null && target.GetParent() is Node2D parent)
			health = parent.GetNodeOrNull<HealthComponent>("HealthComponent");

		if (health == null) return false;

		Vector2 knockback = (target.GlobalPosition - GlobalPosition).Normalized();
		health.Damage(Damage, knockback);

		if (target is Sam player)
		{
			player.ApplyBurning(BurnDuration);
		}

		return true;
	}
}
