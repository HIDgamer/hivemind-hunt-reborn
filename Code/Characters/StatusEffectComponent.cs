using Godot;

public partial class StatusEffectComponent : Area2D
{
	public enum StatusEffect
	{
		Freeze,
		Slow
	}

	[Export] public StatusEffect Effect { get; set; } = StatusEffect.Freeze;
	[Export] public float SpeedMultiplier { get; set; } = 0.45f;
	[Export] public float Duration { get; set; } = 1.25f;
	[Export] public bool ApplyOnTouch { get; set; } = true;

	[Signal] public delegate void StatusEffectAppliedEventHandler(Node2D target, StatusEffect effect, float strength, float duration);

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (ApplyOnTouch && body is Sam player)
		{
			ApplyTo(player);
		}
	}

	public void ApplyTo(Sam player)
	{
		if (player == null) return;

		// Freeze and Slow both currently reduce movement speed; they're kept
		// as distinct enum values so hazards can be authored with intent
		// (a "you're frozen" trap reads differently from "sticky floor")
		// even before their effects diverge further.
		if (Effect == StatusEffect.Freeze || Effect == StatusEffect.Slow)
		{
			player.ApplyMovementSlow(SpeedMultiplier, Duration);
		}

		EmitSignal(SignalName.StatusEffectApplied, player, (int)Effect, SpeedMultiplier, Duration);
	}
}
