using Godot;

// Sweeps every Light2D (PointLight2D/DirectionalLight2D alike) in the current
// scene and applies GameSettings.ShadowQuality to it: Off skips shadow
// casting entirely (cheapest — no shadow computation at all), Hard/Soft/
// Smooth map directly to Light2D.ShadowFilter's own None/Pcf5/Pcf13 options.
// Unlike LineOfSightMode (which needs the whole overlay rebuilt, so it only
// takes effect next level load), a shadow-quality change is just a handful
// of property writes per light — cheap enough to re-sweep and apply live the
// moment the setting changes, no reload needed.
public partial class GraphicsQualityApplier : Node
{
	private const string OriginalShadowMetaKey = "_los_had_shadow";

	public override void _Ready()
	{
		ApplyShadowQuality();

		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		if (settings != null)
		{
			settings.SettingsChanged += ApplyShadowQuality;
		}
	}

	private void ApplyShadowQuality()
	{
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		int quality = settings?.ShadowQuality ?? 2;

		Node scene = GetTree().CurrentScene;
		if (scene != null) SweepLights(scene, quality);
	}

	private void SweepLights(Node node, int quality)
	{
		if (node is Light2D light) ApplyToLight(light, quality);
		foreach (Node child in node.GetChildren())
		{
			SweepLights(child, quality);
		}
	}

	private void ApplyToLight(Light2D light, int quality)
	{
		// Cache whatever this light was actually authored with, before the
		// very first time this sweep ever touches it — so a light that was
		// deliberately built without a shadow (a pure ambient glow) doesn't
		// suddenly grow one just because quality got turned up, and Off
		// doesn't permanently forget what the light's real setting was.
		if (!light.HasMeta(OriginalShadowMetaKey))
		{
			light.SetMeta(OriginalShadowMetaKey, light.ShadowEnabled);
		}
		bool authoredShadow = (bool)light.GetMeta(OriginalShadowMetaKey);

		if (quality == 0)
		{
			light.ShadowEnabled = false;
			return;
		}

		light.ShadowEnabled = authoredShadow;
		light.ShadowFilter = quality switch
		{
			1 => Light2D.ShadowFilterEnum.None,
			3 => Light2D.ShadowFilterEnum.Pcf13,
			_ => Light2D.ShadowFilterEnum.Pcf5, // 2, Soft
		};
	}
}
