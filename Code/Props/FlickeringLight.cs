using Godot;

// Drives PointLight2D energy on a light fixture to sell a decaying-ship mood.
// Two independent profiles, picked per instance in the Inspector:
//   AmbientFlicker — continuous noise-driven dimming, reads as a dying fixture.
//   SparkBurst     — steady light, punctuated by rare bright snaps with a
//                     particle pop and (optional) sound, reads as a fault.
// Steady is the no-op default for fixtures that shouldn't flicker at all.
//
// Uses FastNoiseLite instead of raw random per frame so the flicker has a
// coherent shape (rises and falls) rather than white-noise jitter — the same
// approach CameraSystem.gd already uses for its screen-shake noise.
public partial class FlickeringLight : Node2D
{
	public enum FlickerProfile { Steady, AmbientFlicker, SparkBurst }

	[Export] public FlickerProfile Profile = FlickerProfile.AmbientFlicker;

	[ExportGroup("Ambient Flicker")]
	[Export] public float FlickerSpeed = 1.4f;      // Noise scroll rate
	[Export] public float FlickerDepth = 0.45f;      // 0..1 — how far energy can dip
	[Export] public float MinEnergyScale = 0.15f;    // Never fully blacks out
	[Export] public int NoiseSeed = 0;               // 0 = randomized per instance

	[ExportGroup("Spark Burst")]
	[Export] public float SparkIntervalMin = 3.0f;
	[Export] public float SparkIntervalMax = 9.0f;
	// 2.2 read as a literal flashbang with physical light units + HDR glow
	// enabled (project.godot) — a spark should read as a quick bright pop,
	// not a screen-wide white flash.
	[Export] public float SparkFlashEnergyScale = 1.4f;
	[Export] public float SparkFlashDuration = 0.12f;
	[Export] public AudioStream SparkSound;
	// Optional jagged lightning-bolt visual (an "ArcLine" Line2D child) that
	// snaps into a fresh random zigzag every spark and disappears again once
	// the flash ends — a soft particle puff alone didn't read as "electric,"
	// this is what actually sells a Tesla-coil-style arc discharge. Purely
	// additive: hazards without an ArcLine child are unaffected.
	[Export] public float ArcReachDistance = 48f;
	[Export] public float ArcJitter = 10f;
	[Export] public int ArcSegments = 6;

	private PointLight2D _beam;
	private PointLight2D _aura;
	private CpuParticles2D _sparkParticles;
	private AudioStreamPlayer2D _sparkAudio;
	private Line2D _arcLine;

	private float _beamBaseEnergy = 1f;
	private float _auraBaseEnergy = 1f;
	private FastNoiseLite _noise;
	private float _noiseTime;
	private float _sparkTimer;
	private float _sparkFlashTimer;
	private RandomNumberGenerator _rng = new();

	public override void _Ready()
	{
		_beam = GetNodeOrNull<PointLight2D>("PointLight2D");
		_aura = GetNodeOrNull<PointLight2D>("Aura");
		_sparkParticles = GetNodeOrNull<CpuParticles2D>("SparkParticles");
		_sparkAudio = GetNodeOrNull<AudioStreamPlayer2D>("SparkAudio");
		_arcLine = GetNodeOrNull<Line2D>("ArcLine");
		if (_arcLine != null) _arcLine.Visible = false;

		if (_beam != null) _beamBaseEnergy = _beam.Energy;
		if (_aura != null) _auraBaseEnergy = _aura.Energy;

		if (_sparkAudio != null && SparkSound != null)
			_sparkAudio.Stream = SparkSound;

		_rng.Randomize();

		_noise = new FastNoiseLite();
		_noise.Seed = NoiseSeed != 0 ? NoiseSeed : (int)_rng.Randi();
		_noise.Frequency = 0.6f;
		// Desync instances sharing the same profile so a row of lights doesn't
		// pulse in lockstep.
		_noiseTime = _rng.RandfRange(0f, 1000f);

		if (Profile == FlickerProfile.SparkBurst)
			_sparkTimer = _rng.RandfRange(SparkIntervalMin, SparkIntervalMax);
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;

		switch (Profile)
		{
			case FlickerProfile.AmbientFlicker:
				TickAmbientFlicker(dt);
				break;
			case FlickerProfile.SparkBurst:
				TickSparkBurst(dt);
				break;
			// Steady: nothing to do, lights keep their authored energy.
		}
	}

	private void TickAmbientFlicker(float dt)
	{
		_noiseTime += dt * FlickerSpeed;
		float n = (_noise.GetNoise1D(_noiseTime) + 1f) * 0.5f; // 0..1
		float scale = Mathf.Max(Mathf.Lerp(1f - FlickerDepth, 1f, n), MinEnergyScale);
		ApplyEnergyScale(scale);
	}

	private void TickSparkBurst(float dt)
	{
		if (_sparkFlashTimer > 0f)
		{
			_sparkFlashTimer -= dt;
			ApplyEnergyScale(SparkFlashEnergyScale);
			if (_sparkFlashTimer <= 0f)
			{
				ApplyEnergyScale(1f);
				if (_arcLine != null) _arcLine.Visible = false;
			}
			return;
		}

		_sparkTimer -= dt;
		if (_sparkTimer <= 0f)
			FireSpark();
	}

	private void FireSpark()
	{
		_sparkFlashTimer = SparkFlashDuration;
		_sparkTimer = _rng.RandfRange(SparkIntervalMin, SparkIntervalMax);

		if (_arcLine != null)
		{
			GenerateArc();
			_arcLine.Visible = true;
		}

		_sparkParticles?.Restart();
		if (_sparkAudio != null && _sparkAudio.Stream != null)
			_sparkAudio.Play();
	}

	// A fresh random zigzag every flash — same reach each time (authored via
	// ArcReachDistance, along the node's local +X), but the midpoints jitter
	// perpendicular to that line so consecutive sparks don't look identical.
	private void GenerateArc()
	{
		Vector2 start = Vector2.Zero;
		Vector2 end = new Vector2(ArcReachDistance, 0f);
		Vector2 perp = (end - start).Rotated(Mathf.Pi / 2f).Normalized();

		var points = new Vector2[ArcSegments + 1];
		for (int i = 0; i <= ArcSegments; i++)
		{
			float t = (float)i / ArcSegments;
			Vector2 point = start.Lerp(end, t);
			if (i > 0 && i < ArcSegments)
			{
				point += perp * _rng.RandfRange(-ArcJitter, ArcJitter);
			}
			points[i] = point;
		}
		_arcLine.Points = points;
	}

	private void ApplyEnergyScale(float scale)
	{
		if (_beam != null) _beam.Energy = _beamBaseEnergy * scale;
		if (_aura != null) _aura.Energy = _auraBaseEnergy * scale;
	}
}
