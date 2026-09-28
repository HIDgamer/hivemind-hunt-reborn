using Godot;

// The mechanic behind the long-unbound "Flashlight" input action (F) — a
// PointLight2D that follows Sam and continuously aims toward the mouse
// cursor, toggled on/off. Registers itself with LineOfSightSystem so turning
// it on actually pushes back the fog-of-war darkness in the aimed direction,
// not just glowing on top of it.
//
// Must be Node2D, not a plain Node — the PointLight2D child is a CanvasItem
// and needs a CanvasItem ancestor to inherit a transform at all. A plain Node
// in between breaks that chain entirely, which is exactly what "the light
// just spawns in the map and never moves" looked like before this fix.
public partial class FlashlightComponent : Node2D
{
	[Export] public Texture2D ConeTexture;
	[Export] public float Energy = 1.2f;
	[Export] public Color LightColor = new Color(1f, 0.97f, 0.85f, 1f);
	[Export] public float TextureScale = 0.55f;
	[Export] public float SightRadiusTiles = 9f;
	[Export] public bool StartOn = false;
	// FlashlightComponent sits at Sam's node origin, which is near the TOP
	// of her sprite (see JumpParticles at y=16 for her feet) — offset the
	// light down to her center so it spins around her middle, not her head.
	[Export] public Vector2 LightOffset = new Vector2(0f, 8f);
	// Base response speed before GameSettings.MouseSensitivity scales it —
	// the only mouse-driven mechanic in the project today, so this is its
	// sole current consumer.
	[Export] public float BaseAimSpeed = 14f;
	// The cone texture is 256x256 with its tip (the point the beam should
	// spread out FROM) 128px north of the image's own center — i.e. right at
	// the top edge, not the middle. PointLight2D always rotates around the
	// light's own position, and by default draws the texture centered on
	// that same point, so without this the cone visibly spun around its own
	// middle instead of pivoting from its tip like a real held flashlight.
	// PointLight2D.Offset shifts where the image is drawn WITHOUT moving the
	// pivot rotation happens around, so shifting it down by the tip's
	// distance from center brings the tip to the light's actual origin point.
	// Expressed here in raw (unscaled) texture pixels — like TextureScale
	// itself, the engine scales this offset by TextureScale before drawing,
	// so it's applied that way below rather than baked into the constant.
	[Export] public Vector2 TextureOffset = new Vector2(0f, 128f);
	// The cone art's own neutral (Rotation = 0) facing direction isn't East
	// — Vector2.Right.Angle() is 0°, but the artwork itself is drawn facing
	// a different direction at rest, so setting Rotation directly to
	// toMouse.Angle() pointed the cone 90° away from the cursor (mouse due
	// north rendered as facing east). This constant corrects for that: the
	// aim target is toMouse.Angle() + AimOffsetDeg, not toMouse.Angle()
	// alone. -90 is derived from that exact north-shows-as-east symptom;
	// retune here (not in the aiming math) if the art itself changes.
	[Export] public float AimOffsetDeg = -90f;
	// Temporary calibration aid — draws a small magenta dot at the light's
	// actual pivot (its own local origin, which never moves even while
	// _light.Rotation spins the cone around it). Turn on to see exactly
	// where the pivot sits relative to Sam so LightOffset/TextureOffset can
	// be tuned against something visible instead of guessed blind.
	[Export] public bool DebugShowPivot = true;

	private PointLight2D _light;

	public override void _Ready()
	{
		_light = new PointLight2D
		{
			Name = "FlashlightLight",
			Texture = ConeTexture ?? GD.Load<Texture2D>("res://Assets/Light/Light_cone_conical.png"),
			TextureScale = TextureScale,
			Offset = TextureOffset * TextureScale,
			Energy = Energy,
			Color = LightColor,
			Enabled = StartOn,
			Position = LightOffset,
			// Same shadow settings as every other placed light (see
			// light.tscn's PointLight2D) — without this the flashlight was
			// the only light in the game that didn't actually occlude
			// geometry, just glowed through walls.
			ShadowEnabled = true,
			ShadowFilter = Light2D.ShadowFilterEnum.Pcf13,
			ShadowFilterSmooth = 3.0f,
		};
		AddChild(_light);

		if (DebugShowPivot)
		{
			// Child of _light at local (0,0) — the pivot itself — so this
			// dot stays put no matter how _light.Rotation spins the cone.
			var marker = new ColorRect
			{
				Color = Colors.Magenta,
				Size = new Vector2(6f, 6f),
				Position = new Vector2(-3f, -3f),
				ZIndex = 100,
			};
			_light.AddChild(marker);
		}

		// LineOfSightSystem.Instance is safely non-null here — it's set in
		// LineOfSightSystem's own _EnterTree(), which for the whole initial
		// scene batch always completes before any node's _Ready() runs.
		LineOfSightSystem.Instance?.RegisterLight(_light, SightRadiusTiles);
	}

	public override void _ExitTree()
	{
		LineOfSightSystem.Instance?.UnregisterLight(_light);
	}

	public override void _Process(double delta)
	{
		if (Input.IsActionJustPressed("Flashlight"))
		{
			_light.Enabled = !_light.Enabled;
		}

		// Real continuous aiming ("spins around the player") toward the
		// cursor, not just a left/right facing flip. Eased rather than an
		// instant snap so GameSettings.MouseSensitivity has something to
		// scale (higher sensitivity = snappier tracking).
		Vector2 toMouse = GetGlobalMousePosition() - _light.GlobalPosition;
		if (toMouse.LengthSquared() > 0.01f)
		{
			float sensitivity = GetNodeOrNull<GameSettings>("/root/GameSettings")?.MouseSensitivity ?? 1f;
			float t = 1f - Mathf.Exp(-BaseAimSpeed * sensitivity * (float)delta);
			float targetRad = toMouse.Angle() + Mathf.DegToRad(AimOffsetDeg);
			_light.Rotation = Mathf.LerpAngle(_light.Rotation, targetRad, t);
		}
	}
}
