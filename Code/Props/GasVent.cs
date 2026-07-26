using Godot;

// A gas vent, in exactly ONE of three mutually-exclusive roles — mixing
// standalone emission with pipe intake/output on the same instance was
// confusing (an "intake" that also kept quietly emitting its own gas makes
// no physical sense), so Role picks one:
//
//   Emitter — the original "lock the player in a room and let it slowly
//     fill up" puzzle-timer behavior. Vents every frame while Active,
//     completely independent of the pipe network. This is deliberately NOT
//     built on TimedHazardEmitter: that component's telegraph->burst->
//     cooldown shape is right for a periodic zap trap, but far too sparse
//     to ever meaningfully fill a room.
//   Intake — pulls gas OUT of its room and into the pipe network. Does not
//     emit anything itself.
//   Output — releases gas arriving THROUGH the pipe network into its room.
//     Does not emit anything itself.
//
// Doesn't deal exposure damage itself in any role — GasSimulation's own
// exposure check is the single source of truth for "gas hurts you."
public partial class GasVent : Node2D
{
	public enum VentRole { Emitter, Intake, Output }

	[Export] public VentRole Role = VentRole.Emitter;
	[Export] public bool StartActive = true;

	[ExportGroup("Emitter")]
	// Density units per second added at this vent's tile — GasSimulation
	// clamps any single tile to 1.0, so pushing this higher just saturates
	// the source tile (and the tiles around it, via diffusion) faster, not
	// past full. A real slider in the Inspector (not just a number field)
	// specifically so testing/trap-tuning is a quick drag from a weak trickle
	// to a strong gush instead of guessing at numbers. Only used by Role ==
	// Emitter; ignored entirely by Intake/Output.
	[Export(PropertyHint.Range, "0.0,3.0,0.05")] public float EmissionRatePerSecond = 1.0f;
	[Export] public GasSimulation.GasType EmittedGasType = GasSimulation.GasType.Toxic;
	// Degrees — defaults to ambient (a room-temperature leak). Push this up
	// for a hot vent (rises, pools at the ceiling) or down for a cold one
	// (sinks, pools at the floor); see GasSimulation's Heat & Buoyancy group.
	[Export] public float EmittedTemperature = 20f;

	[ExportGroup("Damage")]
	// A destroyed vent (see Break()) permanently stops functioning and
	// switches to the "Broken" SpriteFrames animation already authored on
	// this scene.
	[Export] public string BrokenAnimationName = "Broken";
	// Defaults true — this IS a vent, so a nearby gas explosion (see
	// GasSimulation.BreakNearbyVents) should be able to blow it out. Flip
	// off for a vent you specifically want blast-immune.
	[Export] public bool DestructibleByExplosion = true;

	public bool IsBroken { get; private set; }

	private CpuParticles2D _particles;
	private AudioStreamPlayer2D _audio;
	private AnimatedSprite2D _sprite;
	private bool _active;

	public override void _Ready()
	{
		_particles = GetNodeOrNull<CpuParticles2D>("Particles");
		_audio = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");
		_sprite = GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D");
		SetActive(StartActive);

		if (DestructibleByExplosion) AddToGroup("ExplodableVent");

		// GasSimulation.Instance is safely non-null here — it's set in
		// GasSimulation's own _EnterTree(), which for the whole initial scene
		// batch always completes before any node's _Ready() runs.
		if (Role != VentRole.Emitter) RegisterPipeVent();
	}

	public override void _ExitTree()
	{
		if (Role != VentRole.Emitter && GasSimulation.Instance != null)
		{
			GasSimulation.Instance.UnregisterVent(GasSimulation.Instance.WorldToTile(GlobalPosition));
		}
	}

	// Only actually registers if this vent's tile is genuinely part of a
	// connected pipe network (an "is_vent" Pipes-layer tile GasSimulation's
	// flood fill actually found) — a Role != Emitter vent placed anywhere
	// else is a level-authoring mistake, not something that should silently
	// pretend to work. Warns once and leaves it inert instead.
	private void RegisterPipeVent()
	{
		if (IsBroken || GasSimulation.Instance == null) return;

		Vector2I tile = GasSimulation.Instance.WorldToTile(GlobalPosition);
		if (!GasSimulation.Instance.IsPipeNetworkTile(tile))
		{
			GD.PushWarning($"GasVent '{Name}' is set to Role={Role} but its tile isn't part of any connected pipe network (no 'is_vent'-flagged Pipes-layer tile here) — it will not intake/output anything. Paint a Pipes layer tile with is_vent=true at this position.");
			return;
		}

		if (_active) GasSimulation.Instance.RegisterVent(tile, Role == VentRole.Intake ? GasSimulation.VentMode.Intake : GasSimulation.VentMode.Output);
	}

	public override void _Process(double delta)
	{
		if (Role != VentRole.Emitter || !_active || IsBroken) return;
		GasSimulation.Instance?.EmitGas(GlobalPosition, EmittedGasType, EmissionRatePerSecond * (float)delta, EmittedTemperature);
	}

	// Generic "receive powered state" contract — same method name
	// PressurePlateComponent/Door.cs already use, so a lever or plate that
	// locks the player in a room can turn this on at the same moment,
	// without needing to know it's specifically a gas vent. Ignored once
	// broken — a destroyed vent can't be turned back on remotely.
	public void Powered(bool active) => SetActive(active);

	public void SetActive(bool active)
	{
		if (IsBroken) return;
		_active = active;
		if (_particles != null) _particles.Emitting = active;
		if (_audio != null)
		{
			if (active && !_audio.Playing) _audio.Play();
			else if (!active) _audio.Stop();
		}

		// Unpowering an Intake/Output vent pulls it out of the network
		// (rather than tracking a separate "disabled" flag on the
		// registry) — re-powering re-registers, re-validating its tile
		// each time.
		if (Role != VentRole.Emitter && GasSimulation.Instance != null)
		{
			Vector2I tile = GasSimulation.Instance.WorldToTile(GlobalPosition);
			if (active) RegisterPipeVent();
			else GasSimulation.Instance.UnregisterVent(tile);
		}
	}

	// Permanently destroys this vent — stops emission/piping, drops out of
	// its pipe network (if any), and swaps to the pre-authored "Broken"
	// sprite. Idempotent; safe to call more than once.
	public void Break()
	{
		if (IsBroken) return;
		IsBroken = true;

		_active = false;
		if (_particles != null) _particles.Emitting = false;
		_audio?.Stop();
		if (_sprite != null && _sprite.SpriteFrames != null && _sprite.SpriteFrames.HasAnimation(BrokenAnimationName))
		{
			_sprite.Play(BrokenAnimationName);
		}

		if (Role != VentRole.Emitter && GasSimulation.Instance != null)
		{
			Vector2I tile = GasSimulation.Instance.WorldToTile(GlobalPosition);
			GasSimulation.Instance.SetVentBroken(tile, true);
		}
	}
}
