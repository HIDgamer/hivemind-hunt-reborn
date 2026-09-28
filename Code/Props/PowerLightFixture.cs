using Godot;

// A wall light fixture that task stations (PowerRerouteStation) can switch
// on/off — same "receive powered state" contract Door.cs/GasVent.cs/
// LaserTurret.cs already use. Same node composition as the original
// light.tscn fixture for now (Sprite2D fixture + PointLight2D beam + Aura
// glow, same textures) rather than inventing new art — Powered() just
// toggles both lights and dims the sprite; light.tscn's own flicker/spark
// behavior (FlickeringLight.cs) isn't needed here.
public partial class PowerLightFixture : Node2D
{
	[Export] public bool StartPowered = true;
	[Export] public Color OnModulate = new Color(1f, 1f, 1f, 1f);
	[Export] public Color OffModulate = new Color(0.3f, 0.3f, 0.34f, 1f);

	private Sprite2D _sprite;
	private PointLight2D _light;
	private PointLight2D _aura;

	public override void _Ready()
	{
		_sprite = GetNodeOrNull<Sprite2D>("Sprite2D");
		_light = GetNodeOrNull<PointLight2D>("PointLight2D");
		_aura = GetNodeOrNull<PointLight2D>("Aura");
		Powered(StartPowered);
	}

	// Generic "receive powered state" contract — same method name Door.cs/
	// GasVent.cs/LaserTurret.cs already use, so a power-reroute puzzle (or
	// anything else) can drive a light fixture exactly like it drives a
	// door, vent, or turret.
	public void Powered(bool active)
	{
		if (_light != null) _light.Enabled = active;
		if (_aura != null) _aura.Enabled = active;
		if (_sprite != null) _sprite.Modulate = active ? OnModulate : OffModulate;
	}
}
