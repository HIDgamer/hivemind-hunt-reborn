using Godot;

// A drip/leak point source — mirrors GasVent's Emitter role exactly (a plain
// per-frame EmitX call at a configurable rate). A "drip" is just a low
// EmissionRatePerSecond and a "gush" a high one; no separate discrete-drip
// code path is needed, same reason GasVent doesn't special-case a slow leak
// vs. a fast fill.
public partial class FluidSource : Node2D
{
	[Export] public FluidSimulation.FluidType Type = FluidSimulation.FluidType.Water;
	[Export(PropertyHint.Range, "0.0,3.0,0.05")] public float EmissionRatePerSecond = 0.3f;
	[Export] public bool Active = true;

	[ExportGroup("Drip Visual")]
	[Export] public bool ShowDripParticles = true;
	[Export] public float DripInterval = 1.2f;

	private CpuParticles2D _dripParticles;
	private float _dripTimer;

	public override void _Ready()
	{
		if (!ShowDripParticles) return;

		_dripParticles = new CpuParticles2D
		{
			Emitting = false,
			OneShot = true,
			Amount = 4,
			Lifetime = 0.4,
			Direction = new Vector2(0f, 1f),
			Spread = 12f,
			Gravity = new Vector2(0f, 260f),
			InitialVelocityMin = 8f,
			InitialVelocityMax = 24f,
			ScaleAmountMin = 0.6f,
			ScaleAmountMax = 1f,
			Color = TypeColor(),
		};
		AddChild(_dripParticles);
		_dripTimer = DripInterval;
	}

	public override void _Process(double delta)
	{
		if (!Active) return;

		FluidSimulation.Instance?.EmitFluid(GlobalPosition, Type, EmissionRatePerSecond * (float)delta);

		if (!ShowDripParticles || _dripParticles == null) return;
		_dripTimer -= (float)delta;
		if (_dripTimer <= 0f)
		{
			_dripTimer = DripInterval;
			_dripParticles.Restart();
			_dripParticles.Emitting = true;
		}
	}

	private Color TypeColor() => Type switch
	{
		FluidSimulation.FluidType.Coolant => new Color(0.25f, 0.85f, 0.92f, 0.9f),
		FluidSimulation.FluidType.Blood => new Color(0.32f, 0.03f, 0.03f, 0.9f),
		FluidSimulation.FluidType.Acid => new Color(0.42f, 0.85f, 0.22f, 0.9f),
		_ => new Color(0.16f, 0.4f, 0.62f, 0.9f),
	};
}
