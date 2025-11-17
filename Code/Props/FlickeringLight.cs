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
	[Export] public float SparkFlashEnergyScale = 2.2f;
	[Export] public float SparkFlashDuration = 0.12f;
	[Export] public AudioStream SparkSound;

	private PointLight2D _beam;
	private PointLight2D _aura;
	private CpuParticles2D _sparkParticles;
	private AudioStreamPlayer2D _sparkAudio;

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
				ApplyEnergyScale(1f);
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

		_sparkParticles?.Restart();
		if (_sparkAudio != null && _sparkAudio.Stream != null)
			_sparkAudio.Play();
	}

	private void ApplyEnergyScale(float scale)
	{
		if (_beam != null) _beam.Energy = _beamBaseEnergy * scale;
		if (_aura != null) _aura.Energy = _auraBaseEnergy * scale;
	}
}
