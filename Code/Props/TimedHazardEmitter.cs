using Godot;

// Generic "telegraph -> burst -> cooldown" hazard shared by steam vents, gas
// vents, electrified floor panels, and similar traps — almost all of them are
// the exact same timing shape (warn, then punish, then recover) and only the
// particles/light/sound/damage numbers actually differ per instance. Rather
// than a new script per trap, new trap *types* are new scenes built around
// this one component, configured in the Inspector — same idea as
// FlickeringLight.cs's Profile enum or PressurePlateComponent's generic
// Powered(bool) contract, just applied to "things that hurt you on a timer."
//
// Optional child nodes, all looked up by name and only used if present:
//   Particles              (CpuParticles2D) — Emitting toggled on burst
//   PointLight2D           (PointLight2D)   — pulses during telegraph
//   AudioStreamPlayer2D    (AudioStreamPlayer2D) — plays TelegraphSound then BurstSound
public partial class TimedHazardEmitter : Area2D
{
	[Export] public float IntervalMin = 2.5f;
	[Export] public float IntervalMax = 5.0f;
	[Export] public float TelegraphDuration = 0.6f;
	[Export] public float BurstDuration = 0.8f;
	[Export] public int Damage = 10;
	// Damage repeats while the burst is active (a lingering gas cloud/live
	// wire), not a single hit — set higher than BurstDuration for a
	// one-shot-feeling punch instead of continuous tick damage.
	[Export] public float DamageTickInterval = 0.4f;
	[Export] public AudioStream TelegraphSound;
	[Export] public AudioStream BurstSound;

	[Signal] public delegate void TelegraphStartedEventHandler();
	[Signal] public delegate void BurstStartedEventHandler();
	[Signal] public delegate void BurstEndedEventHandler();

	private enum State { Idle, Telegraphing, Bursting, Cooldown }
	private State _state = State.Idle;
	private float _timer;
	private float _damageTimer;
	private readonly RandomNumberGenerator _rng = new();

	private CpuParticles2D _particles;
	private PointLight2D _light;
	private AudioStreamPlayer2D _audio;
	private float _lightBaseEnergy = 1f;

	public override void _Ready()
	{
		_particles = GetNodeOrNull<CpuParticles2D>("Particles");
		_light = GetNodeOrNull<PointLight2D>("PointLight2D");
		_audio = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");
		if (_light != null) _lightBaseEnergy = _light.Energy;

		_rng.Randomize();
		// Desync instances of the same trap placed near each other so a row
		// of vents doesn't all fire in lockstep.
		_timer = _rng.RandfRange(IntervalMin, IntervalMax);
		SetBurstActive(false);
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		switch (_state)
		{
			case State.Idle:
				_timer -= dt;
				if (_timer <= 0f) StartTelegraph();
				break;

			case State.Telegraphing:
				_timer -= dt;
				if (_light != null)
				{
					// Fast pulse reads as "about to blow" rather than the
					// slow ambient breathing FlickeringLight's own profiles use.
					float pulse = Mathf.Sin(_timer * 30f) * 0.5f + 0.5f;
					_light.Energy = _lightBaseEnergy * Mathf.Lerp(0.6f, 1.4f, pulse);
				}
				if (_timer <= 0f) StartBurst();
				break;

			case State.Bursting:
				_timer -= dt;
				_damageTimer -= dt;
				if (_damageTimer <= 0f)
				{
					_damageTimer = DamageTickInterval;
					DamageOverlapping();
				}
				if (_timer <= 0f) EndBurst();
				break;

			case State.Cooldown:
				_timer -= dt;
				if (_timer <= 0f)
				{
					_state = State.Idle;
					_timer = _rng.RandfRange(IntervalMin, IntervalMax);
				}
				break;
		}
	}

	private void StartTelegraph()
	{
		_state = State.Telegraphing;
		_timer = TelegraphDuration;
		if (_audio != null && TelegraphSound != null)
		{
			_audio.Stream = TelegraphSound;
			_audio.Play();
		}
		EmitSignal(SignalName.TelegraphStarted);
	}

	private void StartBurst()
	{
		_state = State.Bursting;
		_timer = BurstDuration;
		_damageTimer = 0f;
		SetBurstActive(true);
		if (_audio != null && BurstSound != null)
		{
			_audio.Stream = BurstSound;
			_audio.Play();
		}
		EmitSignal(SignalName.BurstStarted);
	}

	private void EndBurst()
	{
		_state = State.Cooldown;
		_timer = _rng.RandfRange(IntervalMin, IntervalMax);
		SetBurstActive(false);
		if (_light != null) _light.Energy = _lightBaseEnergy;
		EmitSignal(SignalName.BurstEnded);
	}

	private void SetBurstActive(bool active)
	{
		if (_particles != null) _particles.Emitting = active;
	}

	// Same "look on the body, then its parent" contract as SparkHazard.cs —
	// works on Sam, enemies, or anything else with a HealthComponent without
	// requiring a specific type.
	private void DamageOverlapping()
	{
		foreach (Node2D body in GetOverlappingBodies())
		{
			HealthComponent health = body.GetNodeOrNull<HealthComponent>("HealthComponent");
			if (health == null && body.GetParent() is Node2D parent)
				health = parent.GetNodeOrNull<HealthComponent>("HealthComponent");
			if (health == null) continue;

			Vector2 knockback = (body.GlobalPosition - GlobalPosition).Normalized();
			health.Damage(Damage, knockback);
		}
	}
}
