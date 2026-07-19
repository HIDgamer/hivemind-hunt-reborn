using Godot;
using System;

public partial class HealthComponent : Node
{
	[Export] public int MaxHealth { get; set; } = 3;
	[Export] public float InvulnerabilityDuration { get; set; } = 0.5f;
	[Export] public float DamageCooldownDuration { get; set; } = 0.3f;

	public int CurrentHealth { get; private set; }
	public bool IsDead { get; private set; }
	public bool IsInvulnerable { get; private set; }

	private float _hurtTimer = 0f;
	private float _damageCooldownTimer = 0f;

	// Signals for other nodes to listen to
	[Signal] public delegate void HealthChangedEventHandler(int currentHealth, int maxHealth);
	[Signal] public delegate void TookDamageEventHandler(int amount, Vector2 knockbackDirection);
	[Signal] public delegate void DiedEventHandler();

	public override void _Ready()
	{
		CurrentHealth = MaxHealth;
	}

	public override void _Process(double delta)
	{
		float deltaTime = (float)delta;

		if (IsInvulnerable)
		{
			_hurtTimer -= deltaTime;
			if (_hurtTimer <= 0) IsInvulnerable = false;
		}

		if (_damageCooldownTimer > 0)
		{
			_damageCooldownTimer -= deltaTime;
		}
	}

	public void Damage(int amount, Vector2 knockbackDirection)
	{
		if (IsInvulnerable || IsDead || _damageCooldownTimer > 0) return;

		CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
		_damageCooldownTimer = DamageCooldownDuration;

		EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);
		
		if (CurrentHealth <= 0)
		{
			IsDead = true;
			EmitSignal(SignalName.Died);
		}
		else
		{
			IsInvulnerable = true;
			_hurtTimer = InvulnerabilityDuration;
			EmitSignal(SignalName.TookDamage, amount, knockbackDirection);
		}
	}

	public void AddTemporaryInvulnerability(float duration)
	{
		if (IsDead || duration <= 0f) return;

		IsInvulnerable = true;
		_hurtTimer = Mathf.Max(_hurtTimer, duration);
	}

	// Resets to full health and clears the dead/invulnerable state — used
	// when respawning at a checkpoint instead of reloading the whole scene.
	public void Revive()
	{
		CurrentHealth = MaxHealth;
		IsDead = false;
		IsInvulnerable = false;
		_hurtTimer = 0f;
		_damageCooldownTimer = 0f;
		EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);
	}
}
