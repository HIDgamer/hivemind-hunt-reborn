using Godot;
using System.Collections.Generic;

// Tile-based liquid — a sibling to GasSimulation reusing its proven
// SETTLE-then-LEVEL flow shape and mask-texture rendering technique, but
// deliberately NOT sharing code with it: liquids need a fundamentally
// different mixture shape (one type per tile, immiscible, no independent
// Flammable/Toxic/Oxygen coexistence) and always sink (no buoyancy
// spectrum/up option at all), so forcing this through GasMixture/its
// buoyancy math would be wrong-shaped, not a refactor.
//
// A tile holds at most ONE FluidType at a time. Liquids of different types
// don't blend on contact — a tile with acid pooled on it blocks water from
// flowing in until the acid clears, same way real immiscible liquids layer
// rather than mix. An empty tile simply adopts whatever type first flows
// into it.
//
// Ticks at TickInterval, same polling-timer convention as GasSimulation, and
// stays essentially free when no fluid exists anywhere (sparse dictionary,
// empty tick is a no-op).
//
// Purely a local rendering/hazard concern, same as GasSimulation/
// LineOfSightSystem — every peer in a networked session simulates and draws
// only its own local puddles, and fluid exposure only ever damages the LOCAL
// player. Not networked, and not a gap: unlike EnvironmentDamage's tile
// destruction, a puddle differing slightly per peer has no gameplay-relevant
// consequence (nothing about it blocks movement or changes collision).
public partial class FluidSimulation : Node2D
{
	public static FluidSimulation Instance { get; private set; }

	public enum FluidType { Water, Coolant, Blood, Acid }

	public struct FluidMixture
	{
		public FluidType Type;
		public float Amount;
	}

	[Export] public NodePath TileMapPath = "../TileMap";
	// Same real-physics-query convention as GasSimulation.SolidCollisionMask
	// — never a hardcoded TileMap layer index, which is exactly the
	// fragility class that once silently broke the Pipes network.
	[Export(PropertyHint.Layers2DPhysics)] public uint SolidCollisionMask = 1;

	[ExportGroup("Flow")]
	[Export] public float TickInterval = 0.14f;
	[Export] public float SettleRate = 0.6f;
	[Export] public float FlowRate = 0.5f;
	[Export] public float MinDepth = 0.0005f;
	// Nonzero, unlike GasSimulation's own DecayRate (which defaults to 0 —
	// "sealed room holds gas forever"). That reasoning doesn't hold here: gas
	// traps are authored inside sealed rooms, but a FluidSource can just as
	// easily sit over a wide-open, fully-connected floor. With zero
	// evaporation a constant-rate source has nothing to reach equilibrium
	// against and eventually floods every reachable tile (confirmed — this
	// is exactly what happened). A small continuous loss proportional to
	// standing depth means a steady source settles at a genuine finite pool
	// size (inflow = evaporation loss), the same way a real puddle under a
	// slow drip does, instead of growing forever.
	[Export] public float EvaporationRate = 0.015f;
	// Safety valve, not the primary fix (EvaporationRate above is) — mirrors
	// GasSimulation.MaxChainReactionTiles. Once _fluid holds this many
	// tracked tiles, flow refuses to claim any NEW tile (existing tiles can
	// still gain/lose amount) so a future misconfiguration (e.g.
	// EvaporationRate set back to 0) degrades gracefully instead of
	// consuming unbounded memory/CPU.
	[Export] public int MaxTrackedTiles = 3000;

	[ExportGroup("Exposure")]
	[Export] public float AcidDamageThreshold = 0.1f;
	[Export] public int AcidDamagePerTick = 1;
	// Paced well past HealthComponent's 0.5s invulnerability window, same
	// lesson GasSimulation's own damage ticks already apply.
	[Export] public float AcidDamageTickInterval = 0.7f;
	[Export] public float WaterSlowMultiplier = 0.75f;

	[ExportGroup("Rendering")]
	[Export] public int WindowRadiusTiles = 16;
	// Trimmed from an earlier 0.85 — a secondary mitigation against this
	// level's aggressive bloom/glow (hdr_2d, 6 glow levels) blowing out a
	// large pool into a whiteout; EvaporationRate above is the primary fix
	// for that (bounding how large a pool can actually get).
	[Export] public float FluidAlpha = 0.7f;
	[Export] public Color WaterColor = new Color(0.16f, 0.4f, 0.62f, 1f);
	[Export] public Color CoolantColor = new Color(0.25f, 0.85f, 0.92f, 1f);
	[Export] public Color BloodColor = new Color(0.32f, 0.03f, 0.03f, 1f);
	[Export] public Color AcidColor = new Color(0.42f, 0.85f, 0.22f, 1f);
	[Export] public float AcidGlowStrength = 1.15f;
	[Export] public float AcidPatternFrequency = 0.12f;
	[Export] public float AcidPatternSpeed = 0.35f;

	// Deliberately simple — a flat per-type tint (same mask-texture
	// technique as GasSimulation/EnvironmentDamage) with a mild per-tile
	// mottle and a slow brightness shimmer for a bit of life, plus the
	// existing acid glow. No heightfield simulation, no reflection/
	// refraction sampling — that was tried and was more than this needed
	// ("way too much for what I asked for").
	private const string ShaderSource = @"
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D mask_texture : filter_linear;
uniform sampler2D acid_noise_texture : repeat_enable, filter_linear;
uniform vec2 sprite_world_size;
uniform vec2 grid_offset_world;
uniform float tile_size;
uniform float grid_span_tiles;
uniform vec4 water_color : source_color = vec4(0.16, 0.4, 0.62, 1.0);
uniform vec4 coolant_color : source_color = vec4(0.25, 0.85, 0.92, 1.0);
uniform vec4 blood_color : source_color = vec4(0.32, 0.03, 0.03, 1.0);
uniform vec4 acid_color : source_color = vec4(0.42, 0.85, 0.22, 1.0);
uniform float fluid_alpha = 0.7;
uniform float acid_glow_strength = 1.15;
uniform float acid_pattern_frequency = 0.12;
uniform float acid_pattern_speed = 0.35;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec2 local_world_offset = (UV - 0.5) * sprite_world_size;
	vec2 offset_from_grid_center = local_world_offset + grid_offset_world;
	vec2 tile_offset = offset_from_grid_center / tile_size;
	vec2 grid_uv = (tile_offset / grid_span_tiles) + 0.5;

	vec4 mask = vec4(0.0);
	if (grid_uv.x >= 0.0 && grid_uv.x <= 1.0 && grid_uv.y >= 0.0 && grid_uv.y <= 1.0) {
		mask = texture(mask_texture, grid_uv);
	}

	float amt = mask.r;
	COLOR = vec4(0.0);

	// Godot's shader language doesn't allow an early `return;` inside
	// fragment() (fails the whole shader to compile, silently falling back
	// to unshaded texture rendering) — so the body is gated behind this
	// single if instead, leaving COLOR at the already-assigned transparent
	// default when there's nothing here.
	if (amt > 0.002) {
		int type_idx = int(round(mask.g * 3.0));
		vec3 base_tint = water_color.rgb;
		if (type_idx == 1) base_tint = coolant_color.rgb;
		else if (type_idx == 2) base_tint = blood_color.rgb;
		else if (type_idx == 3) base_tint = acid_color.rgb;

		vec2 world_coord = offset_from_grid_center / tile_size;
		vec2 world_tile = floor(world_coord);
		float mottle = mix(0.9, 1.1, hash(world_tile));
		float shimmer = 0.92 + 0.08 * sin(TIME * 1.5 + world_tile.x * 0.7 + world_tile.y * 0.7);
		vec3 rgb = base_tint * mottle * shimmer;

		// Acid keeps its pulsing-bubble glow — a smooth FastNoiseLite
		// texture (same technique already proven on Toxic gas), layered
		// over the flat tint.
		if (type_idx == 3) {
			vec2 acid_noise_uv = world_coord * acid_pattern_frequency
				+ vec2(TIME * acid_pattern_speed, TIME * acid_pattern_speed * 0.3);
			float acid_n = texture(acid_noise_texture, acid_noise_uv).r;
			float bubble = smoothstep(0.3, 0.7, acid_n);
			rgb = acid_color.rgb * acid_glow_strength * mix(0.85, 1.1, bubble);
		}

		COLOR = vec4(rgb, clamp(amt * fluid_alpha, 0.0, 1.0));
	}
}
";

	private TileMap _tileMap;
	private Vector2I _tileSize;
	private Dictionary<Vector2I, FluidMixture> _fluid = new();

	private Sprite2D _overlay;
	private ShaderMaterial _material;
	private Image _maskImage;
	private ImageTexture _maskTexture;
	private int _maskResolution;

	private float _tickTimer;
	private float _acidDamageTimer;
	private bool _maxTilesWarned;

	// Own copy, not shared with GasSimulation — same "small enough to
	// duplicate, not worth a base class" precedent EnvironmentDamage already
	// set for its own solidity-adjacent per-tile caches.
	private readonly Dictionary<Vector2I, bool> _solidCache = new();
	private RectangleShape2D _solidProbeShape;

	private static readonly Vector2I DownOffset = new Vector2I(0, 1);
	private static readonly Vector2I[] NeighborOffsets =
	{
		new Vector2I(1, 0), new Vector2I(-1, 0), new Vector2I(0, 1), new Vector2I(0, -1)
	};

	// Instance is set here rather than in _Ready() — Godot runs _EnterTree()
	// for an entire scene batch (parent-first, top-down) before _ready() runs
	// for ANY node in that batch, so setting it here guarantees every sibling
	// consumer's own _Ready() sees a non-null Instance regardless of scene
	// declaration order, instead of racing against this node's own _Ready().
	public override void _EnterTree()
	{
		_tileMap = GetNodeOrNull<TileMap>(TileMapPath);
		if (_tileMap != null) Instance = this;
	}

	public override void _Ready()
	{
		if (_tileMap == null)
		{
			GD.PushWarning($"FluidSimulation: no TileMap found at '{TileMapPath}' — disabling.");
			SetProcess(false);
			return;
		}

		_tileSize = _tileMap.TileSet.TileSize;

		_maskResolution = WindowRadiusTiles * 2 + 1;
		_maskImage = Image.CreateEmpty(_maskResolution, _maskResolution, false, Image.Format.Rgba8);
		_maskTexture = ImageTexture.CreateFromImage(_maskImage);

		_material = new ShaderMaterial { Shader = new Shader { Code = ShaderSource } };
		_material.SetShaderParameter("mask_texture", _maskTexture);
		_material.SetShaderParameter("tile_size", (float)_tileSize.X);
		_material.SetShaderParameter("grid_span_tiles", (float)_maskResolution);
		_material.SetShaderParameter("water_color", WaterColor);
		_material.SetShaderParameter("coolant_color", CoolantColor);
		_material.SetShaderParameter("blood_color", BloodColor);
		_material.SetShaderParameter("acid_color", AcidColor);
		_material.SetShaderParameter("fluid_alpha", FluidAlpha);
		_material.SetShaderParameter("acid_glow_strength", AcidGlowStrength);
		_material.SetShaderParameter("acid_pattern_frequency", AcidPatternFrequency);
		_material.SetShaderParameter("acid_pattern_speed", AcidPatternSpeed);

		// Same FastNoiseLite + NoiseTexture2D convention as GasSimulation's
		// own toxic_noise_texture — a distinct seed so the two don't read as
		// visually synchronized when both are on screen at once.
		var acidNoise = new FastNoiseLite { Frequency = 0.045f, Seed = 8181 };
		var acidNoiseTexture = new NoiseTexture2D { Width = 128, Height = 128, Noise = acidNoise, Seamless = true };
		_material.SetShaderParameter("acid_noise_texture", acidNoiseTexture);

		var placeholder = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		placeholder.SetPixel(0, 0, Colors.White);
		_overlay = new Sprite2D
		{
			Texture = ImageTexture.CreateFromImage(placeholder),
			Centered = true,
			Material = _material,
			// Below GasSimulation's gas overlay (4095) and EnvironmentDamage's
			// scorch (4094) — liquid sits ON the tile surface; gas/smoke
			// drifts above everything, scorch is baked into the surface
			// itself but reads "drier" than a wet pool sitting on top of it.
			ZIndex = 4093,
			ZAsRelative = false,
		};
		AddChild(_overlay);
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}

	// Public API for FluidSource (and any future liquid hazard) — mirrors
	// GasSimulation.EmitGas's shape. A different type already meaningfully
	// pooled on the target tile blocks the incoming amount entirely rather
	// than blending (immiscibility is enforced here too, not just in the
	// flow step, so a source dripping directly onto an existing puddle of a
	// different kind doesn't cheat around the flow-stage check).
	public void EmitFluid(Vector2 worldPosition, FluidType type, float amount)
	{
		if (_tileMap == null || amount <= 0f) return;
		Vector2I tile = _tileMap.LocalToMap(_tileMap.ToLocal(worldPosition));

		_fluid.TryGetValue(tile, out FluidMixture mix);
		if (mix.Amount > MinDepth && mix.Type != type) return;

		if (mix.Amount <= MinDepth && !TryClaimNewTile()) return;

		float updated = Mathf.Min(1f, mix.Amount + amount);
		_fluid[tile] = new FluidMixture { Type = type, Amount = updated };
	}

	// Guards every place a flow/emission would grow _fluid's tracked-tile
	// count — existing tiles can always keep gaining/losing amount, only
	// brand-new ones are refused once at MaxTrackedTiles.
	private bool TryClaimNewTile()
	{
		if (_fluid.Count < MaxTrackedTiles) return true;

		if (!_maxTilesWarned)
		{
			_maxTilesWarned = true;
			GD.PushWarning($"FluidSimulation: MaxTrackedTiles ({MaxTrackedTiles}) reached — no new tiles will be claimed until existing ones evaporate. Check EvaporationRate if this keeps happening.");
		}
		return false;
	}

	public override void _Process(double delta)
	{
		if (_tileMap == null) return;
		float dt = (float)delta;

		_tickTimer -= dt;
		if (_tickTimer <= 0f)
		{
			_tickTimer = TickInterval;
			SimulationTick();
		}

		Sam player = GetLocalPlayer();
		if (player == null) return;

		Camera2D camera = player.GetNodeOrNull<Camera2D>("PlayerCamera");
		if (camera == null || !camera.IsCurrent()) return;

		Vector2 cameraCenter = camera.GetScreenCenterPosition();
		Vector2 visibleWorldSize = (Vector2)GetViewport().GetVisibleRect().Size * camera.Zoom * 1.1f;
		Vector2I windowCenterTile = _tileMap.LocalToMap(_tileMap.ToLocal(cameraCenter));
		Vector2 gridCenterWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(windowCenterTile));

		_overlay.GlobalPosition = cameraCenter;
		_overlay.Scale = visibleWorldSize;
		_material.SetShaderParameter("sprite_world_size", visibleWorldSize);
		_material.SetShaderParameter("grid_offset_world", cameraCenter - gridCenterWorld);

		UpdateFluidTexture(windowCenterTile);
		CheckPlayerFluidExposure(player, dt);
	}

	private void SimulationTick()
	{
		_solidCache.Clear();
		if (_fluid.Count > 0) SimulationTickFluid();
	}

	// Two-stage flow, same shape as GasSimulation.SimulationTickFluid but
	// hardcoded settle-DOWN-only (no buoyancy/up branch — liquids always
	// sink) and with an immiscibility check added to both stages: a flow
	// never enters a tile already holding a meaningfully-different fluid
	// type, so e.g. acid doesn't creep sideways into an existing water pool.
	private void SimulationTickFluid()
	{
		var next = new Dictionary<Vector2I, FluidMixture>();
		var flows = new List<(Vector2I neighbor, float weight)>(4);

		foreach (KeyValuePair<Vector2I, FluidMixture> pair in _fluid)
		{
			FluidMixture mix = pair.Value;
			float amount = mix.Amount;
			if (amount <= MinDepth)
			{
				Accumulate(next, pair.Key, mix);
				continue;
			}

			float remaining = amount;

			// SETTLE — straight down, capacity-capped (not gradient-capped),
			// same "does the floor tile have room" logic as GasSimulation's
			// SettleRate — this is what produces a flat-topped pool instead
			// of an even diffuse haze.
			Vector2I downNeighbor = pair.Key + DownOffset;
			if (!IsSolid(downNeighbor))
			{
				_fluid.TryGetValue(downNeighbor, out FluidMixture downMix);
				bool blocked = downMix.Amount > MinDepth && downMix.Type != mix.Type;
				bool capBlocked = downMix.Amount <= MinDepth && _fluid.Count >= MaxTrackedTiles;
				if (!blocked && !capBlocked)
				{
					float capacity = Mathf.Max(0f, 1f - downMix.Amount);
					float settleAmount = Mathf.Min(amount * SettleRate, capacity);
					if (settleAmount > 0f)
					{
						Accumulate(next, downNeighbor, new FluidMixture { Type = mix.Type, Amount = settleAmount });
						remaining -= settleAmount;
					}
				}
			}

			// LEVEL — whatever's left spreads sideways via gradient
			// diffusion into open, same-type-or-empty neighbors, never the
			// down direction again (already handled above, and excluding it
			// here avoids double-counting the same neighbor twice in one
			// tick).
			flows.Clear();
			float totalGradient = 0f;
			foreach (Vector2I offset in NeighborOffsets)
			{
				if (offset == DownOffset) continue;

				Vector2I neighbor = pair.Key + offset;
				if (IsSolid(neighbor)) continue;

				_fluid.TryGetValue(neighbor, out FluidMixture neighborMix);
				if (neighborMix.Amount > MinDepth && neighborMix.Type != mix.Type) continue;
				if (neighborMix.Amount <= MinDepth && _fluid.Count >= MaxTrackedTiles) continue;

				float gradient = remaining - neighborMix.Amount;
				if (gradient <= 0f) continue;

				flows.Add((neighbor, gradient));
				totalGradient += gradient;
			}

			if (totalGradient <= 0f)
			{
				Accumulate(next, pair.Key, new FluidMixture { Type = mix.Type, Amount = remaining });
				continue;
			}

			// Same per-face-rate normalization as GasSimulation — bounds
			// total outflow to FlowRate's own share of `remaining`
			// regardless of how many sides are open, preventing a
			// full-drain/full-refill oscillation between neighboring tiles.
			float perFaceRate = FlowRate / NeighborOffsets.Length;
			float outflowAmount = Mathf.Min(totalGradient * perFaceRate, remaining);
			float selfRemaining = remaining - outflowAmount;
			Accumulate(next, pair.Key, new FluidMixture { Type = mix.Type, Amount = selfRemaining });

			foreach ((Vector2I neighbor, float weight) in flows)
			{
				float portion = outflowAmount * (weight / totalGradient);
				Accumulate(next, neighbor, new FluidMixture { Type = mix.Type, Amount = portion });
			}
		}

		ApplyDecay(next);
	}

	private static void Accumulate(Dictionary<Vector2I, FluidMixture> dict, Vector2I key, FluidMixture amount)
	{
		if (amount.Amount <= 0f) return;
		dict.TryGetValue(key, out FluidMixture existing);
		FluidType type = existing.Amount > 0f ? existing.Type : amount.Type;
		dict[key] = new FluidMixture { Type = type, Amount = existing.Amount + amount.Amount };
	}

	private void ApplyDecay(Dictionary<Vector2I, FluidMixture> next)
	{
		var final = new Dictionary<Vector2I, FluidMixture>();
		foreach (KeyValuePair<Vector2I, FluidMixture> pair in next)
		{
			float decayedAmount = pair.Value.Amount * (1f - EvaporationRate);
			if (decayedAmount > MinDepth)
			{
				final[pair.Key] = new FluidMixture { Type = pair.Value.Type, Amount = decayedAmount };
			}
		}
		_fluid = final;
	}

	// Own copy of GasSimulation's real-physics-query solidity check — cached
	// per-tile, cleared once per SimulationTick, not per query.
	private bool IsSolid(Vector2I tile)
	{
		if (_solidCache.TryGetValue(tile, out bool cached)) return cached;

		_solidProbeShape ??= new RectangleShape2D { Size = (Vector2)_tileSize * 0.9f };
		Vector2 worldCenter = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
		var query = new PhysicsShapeQueryParameters2D
		{
			Shape = _solidProbeShape,
			Transform = new Transform2D(0f, worldCenter),
			CollisionMask = SolidCollisionMask,
			CollideWithBodies = true,
			CollideWithAreas = false,
		};

		bool solid = GetWorld2D().DirectSpaceState.IntersectShape(query, 1).Count > 0;
		_solidCache[tile] = solid;
		return solid;
	}

	private void UpdateFluidTexture(Vector2I windowCenterTile)
	{
		for (int gy = 0; gy < _maskResolution; gy++)
		{
			int tileY = windowCenterTile.Y - WindowRadiusTiles + gy;
			for (int gx = 0; gx < _maskResolution; gx++)
			{
				int tileX = windowCenterTile.X - WindowRadiusTiles + gx;
				_fluid.TryGetValue(new Vector2I(tileX, tileY), out FluidMixture mix);
				float clampedAmount = Mathf.Min(mix.Amount, 1f);
				float typeChannel = (float)mix.Type / 3f;
				_maskImage.SetPixel(gx, gy, new Color(clampedAmount, typeChannel, 0f, clampedAmount));
			}
		}
		_maskTexture.Update(_maskImage);
	}

	// Acid damages on a scaled tick (same ratio-past-threshold shape as
	// GasSimulation.CheckPlayerExposure); Water/Coolant just slow movement
	// while standing in them, refreshed every frame so the slow naturally
	// expires shortly after leaving instead of needing an explicit
	// un-slow call; Blood is purely cosmetic.
	private void CheckPlayerFluidExposure(Sam player, float dt)
	{
		Vector2I tile = _tileMap.LocalToMap(_tileMap.ToLocal(player.GlobalPosition));
		if (!_fluid.TryGetValue(tile, out FluidMixture mix) || mix.Amount <= MinDepth)
		{
			_acidDamageTimer = 0f;
			return;
		}

		if (mix.Type == FluidType.Acid)
		{
			if (mix.Amount < AcidDamageThreshold)
			{
				_acidDamageTimer = 0f;
				return;
			}

			_acidDamageTimer -= dt;
			if (_acidDamageTimer <= 0f)
			{
				_acidDamageTimer = AcidDamageTickInterval;
				float overThresholdRatio = mix.Amount / AcidDamageThreshold;
				int damage = Mathf.Max(AcidDamagePerTick, Mathf.RoundToInt(AcidDamagePerTick * overThresholdRatio));
				player.GetNodeOrNull<HealthComponent>("HealthComponent")?.Damage(damage, Vector2.Zero);
			}
			return;
		}

		_acidDamageTimer = 0f;
		if (mix.Type == FluidType.Water || mix.Type == FluidType.Coolant)
		{
			player.ApplyMovementSlow(WaterSlowMultiplier, 0.2f);
		}
	}

	private Sam GetLocalPlayer()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is Sam sam && (!sam.IsNetworked || sam.IsMultiplayerAuthority()))
				return sam;
		}
		return null;
	}
}
