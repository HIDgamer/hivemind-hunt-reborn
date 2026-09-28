using Godot;

public partial class HealthComponent : Node
{
	public enum DamageType { Generic, Fire, Acid, Impact, Electric }

	[Export] public int MaxHealth { get; set; } = 3;
	[Export] public float InvulnerabilityDuration { get; set; } = 0.5f;

	[ExportGroup("Armor")]
	// Flat reduction applied to every hit before the Mathf.Max(1, ...) floor
	// below — armor can soften a hit but never fully nullify one, closing
	// off a stack-to-invincibility exploit.
	[Export] public float Armor = 0f;
	// Index-aligned parallel arrays (ResistedTypes[i] -> ResistanceMultipliers[i]),
	// same convention as EnvironmentDamage's DestructibleLayerNames/
	// DestructibleTerrainSets and ObjectManager's SpawnKeys/SpawnScenes —
	// deliberately NOT a Dictionary export, after ObjectManager.Catalog lost
	// data across an editor restart. Both default empty, so nothing
	// configured means fully unchanged (no resistance) behavior.
	// ResistedTypes is int[] (the DamageType enum's underlying value), not
	// DamageType[] — Godot's C# export binding doesn't support arrays of
	// custom enum types (GD0102).
	[Export] public int[] ResistedTypes = [];
	[Export] public float[] ResistanceMultipliers = [];

	[ExportGroup("Multiplayer")]
	// Off by default — most HealthComponent consumers (hazards, destructible
	// props, enemies with their own authority handling) are local-only and
	// shouldn't change behavior. Sam.tscn's instance sets this true: since
	// HealthComponent is a child of Sam, and Sam's own multiplayer authority
	// is already correctly assigned by PlayerSpawner, authority propagates
	// here with no extra plumbing. Without this gate, every peer's local
	// hazard simulation (GetOverlappingBodies() sees a remote puppet's
	// replicated collision shape same as a local player's) could
	// independently call Damage() on a player it doesn't own.
	[Export] public bool RequireMultiplayerAuthority = false;
	// Authority writes this every time CurrentHealth changes; puppets read
	// the already-replicated value and apply it locally — same "authority
	// writes a plain Export mirror property every frame, puppets just read
	// it" idiom Sam.cs already uses for NetAnimationName/NetFlipH/NetFrame.
	// See NetworkPlayer.tscn's SceneReplicationConfig for the wiring.
	[Export] public int SyncedHealth { get; set; }

	public int CurrentHealth { get; private set; }
	public bool IsDead { get; private set; }
	public bool IsInvulnerable { get; private set; }
	// A second, independent invulnerability gate — unlike IsInvulnerable
	// (a brief automatic post-hit i-frame timer, always self-clearing), this
	// is driven entirely by an owning state machine (e.g. a boss setting it
	// true during an attack windup and false while stunned) and stays
	// whatever it was last set to until that owner changes it again. Damage()
	// respects both, so a boss mid-attack can't be chipped down by a stray
	// hazard any more than by a stomp.
	public bool ExternallyInvulnerable { get; set; }

	private float _hurtTimer = 0f;
	private int _lastSyncedHealth = -1;

	// Signals for other nodes to listen to
	[Signal] public delegate void HealthChangedEventHandler(int currentHealth, int maxHealth);
	[Signal] public delegate void TookDamageEventHandler(int amount, Vector2 knockbackDirection);
	[Signal] public delegate void DiedEventHandler();

	public override void _Ready()
	{
		CurrentHealth = MaxHealth;
		SyncedHealth = CurrentHealth;
		_lastSyncedHealth = CurrentHealth;
	}

	public override void _Process(double delta)
	{
		float deltaTime = (float)delta;

		if (IsInvulnerable)
		{
			_hurtTimer -= deltaTime;
			if (_hurtTimer <= 0) IsInvulnerable = false;
		}

		// Puppet side only — the authority path below writes SyncedHealth
		// itself and never needs to read it back. Applies the already-
		// replicated value directly (no cooldown/invulnerability
		// re-processing, that's the authority's concern) and emits the same
		// signals a local Damage()/Heal() call would have, so existing HUD/
		// hurt-VFX wiring works identically whether it's watching the local
		// player or a remote one.
		if (RequireMultiplayerAuthority && Multiplayer.HasMultiplayerPeer() && !IsMultiplayerAuthority() && SyncedHealth != _lastSyncedHealth)
		{
			int previous = _lastSyncedHealth;
			_lastSyncedHealth = SyncedHealth;
			CurrentHealth = SyncedHealth;
			EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);

			if (CurrentHealth <= 0)
			{
				if (!IsDead)
				{
					IsDead = true;
					EmitSignal(SignalName.Died);
				}
			}
			else
			{
				IsDead = false;
				if (CurrentHealth < previous)
				{
					EmitSignal(SignalName.TookDamage, previous - CurrentHealth, Vector2.Zero);
				}
			}
		}
	}

	// Split into two explicit overloads rather than one method with a
	// `DamageType type = DamageType.Generic` default. A default value on a
	// custom enum parameter is a C#-compiler-only feature — it's resolved at
	// the CALLER's compile time, which every GDScript call site (Crusher.gd,
	// Queen.gd, Runner.gd, AcidSpit.gd, EnemyBase.gd's own stomp-kill) can't
	// do, since they go through Godot's dynamic MethodBind dispatch instead.
	// An enum default that doesn't marshal into that binding cleanly can
	// make the whole method fail to register as callable from script at
	// all — surfacing as "Nonexistent function 'Damage'" rather than an
	// argument-count error, exactly what every 2-argument GDScript call site
	// hit. Two genuine overloads sidestep default-value marshaling entirely.
	public void Damage(int amount, Vector2 knockbackDirection) => Damage(amount, knockbackDirection, DamageType.Generic);

	public void Damage(int amount, Vector2 knockbackDirection, DamageType type)
	{
		if (RequireMultiplayerAuthority && Multiplayer.HasMultiplayerPeer() && !IsMultiplayerAuthority()) return;
		if (IsInvulnerable || ExternallyInvulnerable || IsDead) return;

		float scaled = amount * ResistanceMultiplierFor(type);
		int finalAmount = amount > 0 ? Mathf.Max(1, Mathf.RoundToInt(scaled - Armor)) : 0;

		CurrentHealth = Mathf.Max(0, CurrentHealth - finalAmount);
		SyncedHealth = CurrentHealth;
		_lastSyncedHealth = CurrentHealth;

		EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);

		// TookDamage now fires on every successful hit, lethal or not —
		// previously only non-lethal hits emitted it, which meant hurt-
		// flash/SFX/knockback (everything wired to TookDamage) silently
		// skipped on the killing blow. FireEmitterHazard.cs/SparkHazard.cs
		// carry defensive comments about exactly this bug class.
		EmitSignal(SignalName.TookDamage, finalAmount, knockbackDirection);

		if (CurrentHealth <= 0)
		{
			IsDead = true;
			EmitSignal(SignalName.Died);
		}
		else
		{
			IsInvulnerable = true;
			_hurtTimer = InvulnerabilityDuration;
		}
	}

	private float ResistanceMultiplierFor(DamageType type)
	{
		int count = Mathf.Min(ResistedTypes.Length, ResistanceMultipliers.Length);
		for (int i = 0; i < count; i++)
		{
			if (ResistedTypes[i] == (int)type) return ResistanceMultipliers[i];
		}
		return 1f;
	}

	// Partial heal — distinct from IncreaseMaxHealth (permanent cap raise)
	// and Revive (full heal + clears dead/invulnerable state). Not gated by
	// invulnerability, only by IsDead/authority.
	public void Heal(int amount)
	{
		if (RequireMultiplayerAuthority && Multiplayer.HasMultiplayerPeer() && !IsMultiplayerAuthority()) return;
		if (IsDead || amount <= 0) return;

		CurrentHealth = Mathf.Min(MaxHealth, CurrentHealth + amount);
		SyncedHealth = CurrentHealth;
		_lastSyncedHealth = CurrentHealth;
		EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);
	}

	// Permanent health upgrade: raises the cap and heals by the same amount,
	// so picking one up always feels like a gain even at full health.
	public void IncreaseMaxHealth(int amount)
	{
		if (amount <= 0 || IsDead) return;

		MaxHealth += amount;
		CurrentHealth = Mathf.Min(MaxHealth, CurrentHealth + amount);
		SyncedHealth = CurrentHealth;
		_lastSyncedHealth = CurrentHealth;
		EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);
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
		SyncedHealth = CurrentHealth;
		_lastSyncedHealth = CurrentHealth;
		EmitSignal(SignalName.HealthChanged, CurrentHealth, MaxHealth);
	}
}
