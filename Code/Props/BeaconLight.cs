using Godot;

// Rotation/color puppeteer over child PointLight2D(s) — warning/emergency/
// disco beacon behaviors. A sibling to FlickeringLight, not merged into it:
// different concern (rotation/color vs. noise-driven energy), each fully
// independent and composable on separate PointLight2D children if a scene
// wants both.
//
// Spin targets an optional "ConeLight" child (same cone-shaped-texture idea
// FlashlightComponent uses, e.g. Light_cone_conical.png) instead of the main
// PointLight2D when one exists — a plain round glow looks identical no
// matter how it's rotated, so spinning IT never actually reads as
// "spinning." Pulse/ColorCycle always drive the main PointLight2D
// regardless, so a beacon reads as a steady pulsing glow with a distinct
// sweeping beam layered on top, not one light doing both jobs at once. Only
// falls back to spinning the main light itself if no ConeLight child exists,
// preserving the original single-light behavior for any scene still set up
// that way.
public partial class BeaconLight : Node2D
{
	public enum SpinMode { None, Smooth, SteppedAxis }

	[Export] public SpinMode Spin = SpinMode.None;
	[Export] public float SpinSpeedDegPerSec = 90f;
	// SteppedAxis only: how many evenly-spaced directions it snaps between
	// (4 = cardinal N/E/S/W, 8 = + diagonals) and how long it holds each one
	// before snapping to the next — the rigid, mechanical "aims directly at
	// the axes" look, distinct from Smooth's continuous sweep.
	[Export] public int AxisStepCount = 4;
	[Export] public float StepHoldSeconds = 0.4f;

	[ExportGroup("Pulse")]
	// Same cached-base-energy-times-scale idiom FlickeringLight.ApplyEnergyScale
	// already establishes, just driven by a clean sine wave instead of noise —
	// a regular bright/dim pulse reads as "alarm," not "faulty wiring."
	[Export] public bool Pulse = false;
	[Export] public float PulseSpeed = 2f;
	[Export] public float PulseMinEnergyScale = 0.3f;

	[ExportGroup("Color Cycle")]
	[Export] public bool ColorCycle = false;
	[Export] public Gradient ColorCycleGradient;
	[Export] public float ColorCycleSpeed = 1f;

	private PointLight2D _light;
	private PointLight2D _coneLight;
	private float _baseEnergy = 1f;
	private float _pulseTime;
	private float _colorCycleTime;
	private float _stepHoldTimer;
	private int _stepIndex;

	public override void _Ready()
	{
		_light = GetNodeOrNull<PointLight2D>("PointLight2D");
		if (_light != null) _baseEnergy = _light.Energy;

		_coneLight = GetNodeOrNull<PointLight2D>("ConeLight");
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;

		PointLight2D spinTarget = _coneLight ?? _light;
		if (spinTarget != null) TickSpin(spinTarget, dt);

		if (_light == null) return;
		TickPulse(dt);
		TickColorCycle(dt);
	}

	private void TickSpin(PointLight2D target, float dt)
	{
		switch (Spin)
		{
			case SpinMode.Smooth:
				target.Rotation += Mathf.DegToRad(SpinSpeedDegPerSec) * dt;
				break;
			case SpinMode.SteppedAxis:
				TickSteppedAxis(target, dt);
				break;
			// None: leave whatever rotation was authored alone.
		}
	}

	private void TickSteppedAxis(PointLight2D target, float dt)
	{
		int stepCount = Mathf.Max(1, AxisStepCount);
		_stepHoldTimer -= dt;
		if (_stepHoldTimer > 0f) return;

		_stepHoldTimer = StepHoldSeconds;
		_stepIndex = (_stepIndex + 1) % stepCount;
		target.Rotation = Mathf.Tau * _stepIndex / stepCount;
	}

	private void TickPulse(float dt)
	{
		if (!Pulse)
		{
			_light.Energy = _baseEnergy;
			return;
		}

		_pulseTime += dt * PulseSpeed;
		float t = (Mathf.Sin(_pulseTime) + 1f) * 0.5f; // 0..1
		float scale = Mathf.Lerp(PulseMinEnergyScale, 1f, t);
		_light.Energy = _baseEnergy * scale;
	}

	private void TickColorCycle(float dt)
	{
		if (!ColorCycle || ColorCycleGradient == null) return;

		_colorCycleTime += dt * ColorCycleSpeed;
		float t = Mathf.PosMod(_colorCycleTime, 1f);
		_light.Color = ColorCycleGradient.Sample(t);
	}
}
