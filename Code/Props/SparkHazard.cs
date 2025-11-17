using Godot;

// Small continuous damage source for exposed/broken wiring — the "don't
// linger in the sparking wreckage" hazard. Mirrors Laser.cs's convention of
// looking up a HealthComponent on the overlapping body (or its parent)
// rather than requiring a specific type, so it works on Sam, Runner, or
// anything else that grows a HealthComponent later.
public partial class SparkHazard : Area2D
{
	[Export] public int Damage = 5;
	[Export] public double DamageCooldown = 0.6;

	private double _timeSinceLastDamage = 999.0;

	public override void _Process(double delta)
	{
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
		return true;
	}
}
