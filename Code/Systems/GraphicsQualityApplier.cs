using Godot;
using System.Collections.Generic;

// Sweeps every Light2D (PointLight2D/DirectionalLight2D alike) in the current
// scene and applies GameSettings.ShadowQuality to it: Off skips shadow
// casting entirely (cheapest — no shadow computation at all), Hard/Soft/
// Smooth map directly to Light2D.ShadowFilter's own None/Pcf5/Pcf13 options.
// Unlike LineOfSightMode (which needs the whole overlay rebuilt, so it only
// takes effect next level load), a shadow-quality change is just a handful
// of property writes per light — cheap enough to re-sweep and apply live the
// moment the setting changes, no reload needed.
//
// The same sweep also collects every discovered light into _allLights, reused
// by a second, periodic responsibility: culling. Lights are expensive (this
// project runs with physical light units + real shadow casting), so on top of
// GameSettings.ShadowQuality this also (a) disables any light far from the
// camera and (b) caps how many can be enabled at once for the current
// GraphicsPreset tier — nearest-to-camera N stay lit, the rest cull
// regardless of distance.
public partial class GraphicsQualityApplier : Node
{
	private const string OriginalShadowMetaKey = "_los_had_shadow";
	private const string OriginalEnabledMetaKey = "_los_had_enabled";

	[Export] public float CullCheckInterval = 0.3f;
	[Export] public float CullDistance = 900f;

	private readonly List<Light2D> _allLights = new();
	private float _cullTimer;

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

		_allLights.Clear();
		Node scene = GetTree().CurrentScene;
		if (scene != null) SweepLights(scene, quality);
	}

	private void SweepLights(Node node, int quality)
	{
		if (node is Light2D light)
		{
			ApplyToLight(light, quality);
			_allLights.Add(light);
		}
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

	public override void _Process(double delta)
	{
		_cullTimer -= (float)delta;
		if (_cullTimer > 0f) return;
		_cullTimer = CullCheckInterval;

		ApplyCulling();
	}

	// Top tier (Ultra/Custom) gets distance-only culling; everything below
	// also caps the number of simultaneously-enabled lights, nearest to the
	// camera first — a hard budget rather than trusting distance alone to
	// keep a crowded room's light count under control.
	private static int MaxActiveLightsFor(int preset) => preset switch
	{
		0 => 8,  // Low
		1 => 16, // Medium
		2 => 32, // High
		_ => int.MaxValue, // Ultra/Custom — distance cull only
	};

	private void ApplyCulling()
	{
		if (_allLights.Count == 0) return;

		Camera2D camera = GetActiveCamera();
		if (camera == null) return;
		Vector2 cameraPos = camera.GetScreenCenterPosition();

		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		int maxActive = MaxActiveLightsFor(settings?.GraphicsPreset ?? 2);

		// Sort a scratch list by distance so the budget keeps the nearest
		// lights, not an arbitrary tree-order subset.
		var byDistance = new List<(Light2D Light, float DistSq)>(_allLights.Count);
		foreach (Light2D light in _allLights)
		{
			if (!IsInstanceValid(light)) continue;
			if (!light.HasMeta(OriginalEnabledMetaKey))
			{
				light.SetMeta(OriginalEnabledMetaKey, light.Enabled);
			}
			byDistance.Add((light, cameraPos.DistanceSquaredTo(light.GlobalPosition)));
		}
		byDistance.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

		float cullDistSq = CullDistance * CullDistance;
		int activeCount = 0;
		foreach ((Light2D light, float distSq) in byDistance)
		{
			bool authoredEnabled = (bool)light.GetMeta(OriginalEnabledMetaKey);
			if (!authoredEnabled)
			{
				// Never force on a light that started off (e.g. the player's
				// flashlight before its first toggle) — culling only ever
				// turns OFF a light that would otherwise be on.
				continue;
			}

			bool withinBudget = activeCount < maxActive;
			bool withinDistance = distSq <= cullDistSq;
			light.Enabled = withinBudget && withinDistance;
			if (light.Enabled) activeCount++;
		}
	}

	// Same "find my own authority Sam" idiom used throughout this codebase.
	private Camera2D GetActiveCamera()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is not Sam sam || (sam.IsNetworked && !sam.IsMultiplayerAuthority())) continue;
			Camera2D camera = sam.GetNodeOrNull<Camera2D>("PlayerCamera");
			if (camera != null && camera.IsCurrent()) return camera;
		}
		return null;
	}
}
