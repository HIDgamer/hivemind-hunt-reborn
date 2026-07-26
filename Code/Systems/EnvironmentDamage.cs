using Godot;
using System.Collections.Generic;

// Permanent tile scarring — a room that's been through a fire keeps looking
// burnt forever (no decay, unlike GasSimulation's own dictionary, which is
// deliberately transient). Architected identically to GasSimulation/
// LineOfSightSystem (sparse per-tile dictionary + a single world-anchored,
// nearest-filtered mask-texture + shader overlay) since that's a proven,
// low-risk pattern already used twice in this project — kept as its own
// system rather than folded into GasSimulation because the two have
// fundamentally different lifetimes: gas moves and dissipates, scorch never
// does.
//
// Purely a local rendering concern, same as GasSimulation/LineOfSightSystem
// — every peer in a networked session tracks and draws its own scorch state
// near its own camera. Scorch itself (which tiles are charred, how much) is
// NOT currently replicated between peers — acceptable for a single-player-
// primary project; a multiplayer session would see each peer's own view of
// which tiles have burned, same known-gap class as LaserTurret AI.
public partial class EnvironmentDamage : Node2D
{
	public static EnvironmentDamage Instance { get; private set; }

	[Export] public NodePath TileMapPath = "../TileMap";

	[ExportGroup("Scorch")]
	[Export] public float MaxScorch = 1f;
	[Export] public Color ScorchColor = new Color(0.05f, 0.04f, 0.04f, 1f);
	[Export] public float ScorchAlpha = 0.85f;

	[ExportGroup("Smoke")]
	// Once a tile's scorch crosses this, it starts passively smoking — same
	// threshold-crossing convention as GasSimulation's PressureBurst.
	[Export] public float SmokeScorchThreshold = 0.7f;
	// Safety cap — a single big chain-reaction explosion (see
	// GasSimulation.IgniteChain) can heavily char dozens of tiles in one
	// frame; this bounds how many persistent particle emitters exist at
	// once (oldest culled first) rather than letting VFX cost scale
	// unboundedly with how much of the level has burned.
	[Export] public int MaxSmokeEmitters = 24;

	[ExportGroup("Rendering")]
	[Export] public int WindowRadiusTiles = 16;

	[ExportGroup("Destruction")]
	[Export] public bool DestructionEnabled = true;
	// Resolved BY NAME in _Ready (see ResolveLayerByName), not trusted as
	// raw indices — this is exactly the fragility class that just bit
	// GasSimulation's PipeLayer (a level's TileMap layers got reordered and
	// a hardcoded index silently started meaning a different layer). A tile
	// coordinate can be destructible on more than one of these at once
	// (e.g. a decorative layer stacked on a structural one) — ApplyStructuralDamage/
	// DestroyTile check/clear every layer in this list, not just the first match.
	[Export] public string[] DestructibleLayerNames = ["Solid", "Background", "Pipes"];
	// Which terrain set to recompute neighbors against on destroy, PER
	// LAYER (index-aligned with DestructibleLayerNames — index i's terrain
	// set applies to DestructibleLayerNames[i]) — a property of the placed
	// TILES on a layer, not the layer itself, so this has to be told rather
	// than auto-detected, and different layers genuinely use different
	// terrain sets/tilesets in this level: "Solid" draws from the Floor
	// atlas (terrain_set 0), "Background" from the Background atlas
	// (terrain_set 1), "Pipes" from the Props atlas (terrain_set 2). If a
	// layer is missing an entry here it falls back to 0.
	[Export] public int[] DestructibleTerrainSets = [0, 1, 2];
	// Boolean custom data layer on the SAME TileSet tiles, same convention
	// as GasSimulation's "is_vent"/"pipe_leak" — a tile with this unset can
	// NEVER take structural damage or be destroyed, regardless of how much
	// scorch/fire it's seen. Opt-in per tile, not per layer: nothing is
	// flagged by default, so existing level geometry is unaffected until
	// specific walls are deliberately painted breakable.
	[Export] public string IsDestructibleDataName = "destructible";
	[Export] public float TileDestroyThreshold = 1f;

	[Signal] public delegate void TileDestroyedEventHandler(Vector2I tile, Vector2 worldPosition);

	private TileMap _tileMap;
	private Vector2I _tileSize;
	private readonly Dictionary<Vector2I, float> _scorch = new();
	private readonly List<int> _destructibleLayerIndices = new();
	private readonly Dictionary<Vector2I, float> _structuralDamage = new();
	private readonly HashSet<Vector2I> _destroyedTiles = new();

	private Sprite2D _overlay;
	private ShaderMaterial _material;
	private Image _maskImage;
	private ImageTexture _maskTexture;
	private int _maskResolution;

	private readonly Dictionary<Vector2I, CpuParticles2D> _smokeEmitters = new();
	private readonly List<Vector2I> _smokeOrder = new();

	// Tile-aligned, nearest-filtered, world-anchored — same rendering
	// convention GasSimulation's own overlay shader established (no
	// screen-space UV sampling, which is what made an earlier version of
	// the gas visual appear to drift with the camera).
	private const string ShaderSource = @"
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D mask_texture : filter_nearest;
uniform vec2 sprite_world_size;
uniform vec2 grid_offset_world;
uniform float tile_size;
uniform float grid_span_tiles;
uniform vec4 scorch_color : source_color = vec4(0.05, 0.04, 0.04, 1.0);
uniform float scorch_alpha = 0.85;

void fragment() {
	vec2 local_world_offset = (UV - 0.5) * sprite_world_size;
	vec2 offset_from_grid_center = local_world_offset + grid_offset_world;
	vec2 tile_offset = offset_from_grid_center / tile_size;
	vec2 grid_uv = (tile_offset / grid_span_tiles) + 0.5;

	float scorch = 0.0;
	if (grid_uv.x >= 0.0 && grid_uv.x <= 1.0 && grid_uv.y >= 0.0 && grid_uv.y <= 1.0) {
		scorch = texture(mask_texture, grid_uv).r;
	}

	COLOR = vec4(scorch_color.rgb, clamp(scorch * scorch_alpha, 0.0, 1.0));
}
";

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
			GD.PushWarning($"EnvironmentDamage: no TileMap found at '{TileMapPath}' — disabling.");
			SetProcess(false);
			return;
		}

		_tileSize = _tileMap.TileSet.TileSize;
		_destructibleLayerIndices.Clear();
		foreach (string layerName in DestructibleLayerNames)
		{
			_destructibleLayerIndices.Add(ResolveLayerByName(layerName));
		}

		_maskResolution = WindowRadiusTiles * 2 + 1;
		_maskImage = Image.CreateEmpty(_maskResolution, _maskResolution, false, Image.Format.Rgba8);
		_maskTexture = ImageTexture.CreateFromImage(_maskImage);

		_material = new ShaderMaterial { Shader = new Shader { Code = ShaderSource } };
		_material.SetShaderParameter("mask_texture", _maskTexture);
		_material.SetShaderParameter("tile_size", (float)_tileSize.X);
		_material.SetShaderParameter("grid_span_tiles", (float)_maskResolution);
		_material.SetShaderParameter("scorch_color", ScorchColor);
		_material.SetShaderParameter("scorch_alpha", ScorchAlpha);

		var placeholder = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		placeholder.SetPixel(0, 0, Colors.White);
		_overlay = new Sprite2D
		{
			Texture = ImageTexture.CreateFromImage(placeholder),
			Centered = true,
			Material = _material,
			// Below both LineOfSightSystem's darkness (4096) and
			// GasSimulation's own gas overlay (4095) — scorch lives on the
			// actual tile surface, so it should never visually sit "above"
			// a gas cloud drifting past it.
			ZIndex = 4094,
			ZAsRelative = false,
		};
		AddChild(_overlay);
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}

	// Called by any fire/explosion source (GasSimulation's explosions,
	// Laser.cs's sustained beam contact) to permanently char tiles at/near
	// worldPosition. Additive — repeated hits keep darkening a tile up to
	// MaxScorch rather than resetting, so a spot burned twice reads as more
	// damaged than one only caught once. Falls off linearly toward the edge
	// of radius, same idea as a blast losing intensity with distance.
	public void ApplyScorch(Vector2 worldPosition, float amount, float radius)
	{
		if (_tileMap == null) return;
		Vector2I center = _tileMap.LocalToMap(_tileMap.ToLocal(worldPosition));
		int tileRadius = Mathf.Max(0, Mathf.CeilToInt(radius / _tileSize.X));

		for (int dy = -tileRadius; dy <= tileRadius; dy++)
		{
			for (int dx = -tileRadius; dx <= tileRadius; dx++)
			{
				Vector2I tile = center + new Vector2I(dx, dy);
				Vector2 tileWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
				float dist = tileWorld.DistanceTo(worldPosition);
				if (dist > radius) continue;

				float falloff = radius > 0f ? 1f - (dist / radius) : 1f;
				_scorch.TryGetValue(tile, out float current);
				float updated = Mathf.Min(MaxScorch, current + amount * falloff);
				_scorch[tile] = updated;

				if (updated >= SmokeScorchThreshold) EnsureSmoke(tile);
			}
		}
	}

	private int ResolveLayerByName(string layerName)
	{
		int count = _tileMap.GetLayersCount();
		for (int i = 0; i < count; i++)
		{
			if (_tileMap.GetLayerName(i) == layerName) return i;
		}
		GD.PushWarning($"EnvironmentDamage: no TileMap layer named '{layerName}' found — tile destruction will target layer 0, which is almost certainly wrong. Check DestructibleLayerName.");
		return 0;
	}

	// Structural damage — separate from ApplyScorch's cosmetic char level on
	// purpose. Scorch is tuned for visual opacity and never destroys
	// anything on its own (a laser resting on a wall keeps charring it
	// forever, unchanged); this is what actually tracks progress toward a
	// tile being destroyed, and only for tiles on DestructibleLayerName
	// explicitly flagged IsDestructibleDataName. Same radius/falloff shape
	// as ApplyScorch.
	public void ApplyStructuralDamage(Vector2 worldPosition, float amount, float radius)
	{
		if (!DestructionEnabled || _tileMap == null) return;
		Vector2I center = _tileMap.LocalToMap(_tileMap.ToLocal(worldPosition));
		int tileRadius = Mathf.Max(0, Mathf.CeilToInt(radius / _tileSize.X));

		for (int dy = -tileRadius; dy <= tileRadius; dy++)
		{
			for (int dx = -tileRadius; dx <= tileRadius; dx++)
			{
				Vector2I tile = center + new Vector2I(dx, dy);
				if (_destroyedTiles.Contains(tile) || !IsDestructibleTile(tile)) continue;

				Vector2 tileWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
				float dist = tileWorld.DistanceTo(worldPosition);
				if (dist > radius) continue;

				float falloff = radius > 0f ? 1f - (dist / radius) : 1f;
				_structuralDamage.TryGetValue(tile, out float current);
				float updated = current + amount * falloff;
				_structuralDamage[tile] = updated;

				if (updated >= TileDestroyThreshold) BroadcastDestroyTile(tile);
			}
		}
	}

	// True if ANY of DestructibleLayerNames has a destructible-flagged tile
	// at this coordinate — a position can be destructible on more than one
	// layer at once (e.g. a decorative layer stacked on a structural one).
	private bool IsDestructibleTile(Vector2I tile)
	{
		foreach (int layer in _destructibleLayerIndices)
		{
			if (_tileMap.GetCellSourceId(layer, tile) == -1) continue;
			TileData tileData = _tileMap.GetCellTileData(layer, tile);
			if (tileData != null && tileData.GetCustomData(IsDestructibleDataName).AsBool()) return true;
		}
		return false;
	}

	// Destruction is gameplay-relevant (it permanently removes collision, not
	// just a visual char level like ApplyScorch), so — unlike this system's
	// otherwise fully local rendering — the RESULT converges across every
	// peer instead of each one deciding independently. Genuinely networking
	// the CAUSE (GasSimulation's explosion/ignition detection isn't synced
	// either, and gas density can differ per peer) is a much bigger job;
	// broadcasting "this exact tile got destroyed" the moment any peer's
	// local damage accumulation first crosses the threshold sidesteps that
	// entirely — every peer ends up with the same destroyed-tile set
	// regardless of whose local simulation triggered it first.
	private void BroadcastDestroyTile(Vector2I tile)
	{
		if (!Multiplayer.HasMultiplayerPeer())
		{
			DestroyTile(tile);
			return;
		}

		Rpc(MethodName.RemoteDestroyTile, tile.X, tile.Y);
		// Applied locally too, immediately — the triggering peer shouldn't
		// wait on its own RPC round-trip to see its own explosion's result.
		DestroyTile(tile);
	}

	// AnyPeer, not Authority-only — any peer's local damage accumulation can
	// be the first to cross the threshold, not just the host.
	[Rpc(MultiplayerApi.RpcMode.AnyPeer)]
	private void RemoteDestroyTile(int x, int y) => DestroyTile(new Vector2I(x, y));

	// Erases the cell on EVERY destructible layer that actually has
	// destructible content there via SetCellsTerrainConnect (terrain = -1)
	// rather than a plain EraseCell — this is what makes Godot recompute
	// the autotile bitmask of every NEIGHBORING cell too, so a wall next to
	// the gap updates to a correct open edge instead of showing a stale
	// seam that still expects a neighbor that's no longer there. TileMap's
	// physics collision regenerates automatically as part of this, no
	// extra work.
	private void DestroyTile(Vector2I tile)
	{
		// Idempotent — near-simultaneous broadcasts from two peers that both
		// happened to cross the threshold at once, or a peer receiving the
		// RPC for a tile it already destroyed locally itself, are harmless
		// no-ops instead of double-spawning debris/re-emitting the signal.
		if (_destroyedTiles.Contains(tile)) return;

		_destroyedTiles.Add(tile);
		_structuralDamage.Remove(tile);

		var cells = new Godot.Collections.Array<Vector2I> { tile };
		for (int i = 0; i < _destructibleLayerIndices.Count; i++)
		{
			int layer = _destructibleLayerIndices[i];
			if (_tileMap.GetCellSourceId(layer, tile) == -1) continue;
			TileData tileData = _tileMap.GetCellTileData(layer, tile);
			if (tileData == null || !tileData.GetCustomData(IsDestructibleDataName).AsBool()) continue;

			int terrainSet = i < DestructibleTerrainSets.Length ? DestructibleTerrainSets[i] : 0;
			_tileMap.SetCellsTerrainConnect(layer, cells, terrainSet, -1);
		}

		Vector2 worldPosition = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
		SpawnDebris(worldPosition);
		EmitSignal(SignalName.TileDestroyed, tile, worldPosition);
	}

	// Simple, code-built one-shot debris burst — "nothing fancy," a handful
	// of small gritty specks that pop outward and fall with real gravity
	// (unlike the ignition burst's radial particles, these should visibly
	// drop like broken pieces, not hang in the air). Same "code-built,
	// parent to current scene, self-cleanup" convention as every other
	// one-shot VFX in this project (BurnableComponent.SpawnBurnBurst,
	// GasSimulation's ignition burst). Public so GasSimulation's explosions
	// can reuse it too instead of duplicating a second particle setup.
	public void SpawnDebris(Vector2 worldPosition, float scale = 1f, Color? tint = null)
	{
		Node parent = GetTree().CurrentScene;
		if (parent == null) return;

		Color debrisColor = tint ?? new Color(0.35f, 0.33f, 0.3f, 1f);
		var debrisRamp = new Gradient();
		debrisRamp.SetColor(0, debrisColor);
		debrisRamp.SetColor(1, new Color(debrisColor.R * 0.6f, debrisColor.G * 0.6f, debrisColor.B * 0.6f, 0f));

		var debris = new CpuParticles2D
		{
			Emitting = true,
			OneShot = true,
			Explosiveness = 0.9f,
			Amount = Mathf.RoundToInt(10 * scale),
			Lifetime = 0.8f,
			Randomness = 0.5f,
			Texture = GD.Load<Texture2D>("res://Assets/FX/particles/alpha/dirt_02_a.png"),
			EmissionShape = CpuParticles2D.EmissionShapeEnum.Sphere,
			EmissionSphereRadius = 4f * scale,
			Spread = 180f,
			Gravity = new Vector2(0f, 280f),
			InitialVelocityMin = 40f * scale,
			InitialVelocityMax = 110f * scale,
			AngularVelocityMin = -180f,
			AngularVelocityMax = 180f,
			ScaleAmountMin = 0.03f * scale,
			ScaleAmountMax = 0.06f * scale,
			ColorRamp = debrisRamp,
			GlobalPosition = worldPosition,
			ZIndex = 15,
		};
		parent.AddChild(debris);
		GetTree().CreateTimer(1.2).Timeout += () => { if (IsInstanceValid(debris)) debris.QueueFree(); };
	}

	public override void _Process(double delta)
	{
		if (_tileMap == null) return;

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

		UpdateScorchTexture(windowCenterTile);
	}

	private void UpdateScorchTexture(Vector2I windowCenterTile)
	{
		for (int gy = 0; gy < _maskResolution; gy++)
		{
			int tileY = windowCenterTile.Y - WindowRadiusTiles + gy;
			for (int gx = 0; gx < _maskResolution; gx++)
			{
				int tileX = windowCenterTile.X - WindowRadiusTiles + gx;
				_scorch.TryGetValue(new Vector2I(tileX, tileY), out float scorch);
				_maskImage.SetPixel(gx, gy, new Color(scorch, 0f, 0f, 1f));
			}
		}
		_maskTexture.Update(_maskImage);
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

	// Persistent (not one-shot) black smoke — once spawned it just keeps
	// gently drifting off the tile indefinitely, same code-built-particle
	// convention as BurnableComponent.SpawnBurnBurst/GasSimulation's own
	// ignition burst, but Emitting stays true instead of a single burst.
	private void EnsureSmoke(Vector2I tile)
	{
		if (_smokeEmitters.ContainsKey(tile)) return;

		if (_smokeEmitters.Count >= MaxSmokeEmitters)
		{
			Vector2I oldest = _smokeOrder[0];
			_smokeOrder.RemoveAt(0);
			if (_smokeEmitters.TryGetValue(oldest, out CpuParticles2D oldEmitter))
			{
				if (IsInstanceValid(oldEmitter)) oldEmitter.QueueFree();
				_smokeEmitters.Remove(oldest);
			}
		}

		Node parent = GetTree().CurrentScene;
		if (parent == null) return;

		Vector2 worldPosition = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));

		var smokeRamp = new Gradient();
		smokeRamp.SetColor(0, new Color(0.05f, 0.05f, 0.05f, 0.6f));
		smokeRamp.SetColor(1, new Color(0.02f, 0.02f, 0.02f, 0f));

		var smoke = new CpuParticles2D
		{
			Emitting = true,
			Amount = 6,
			Lifetime = 2.2f,
			Randomness = 0.6f,
			Texture = GD.Load<Texture2D>("res://Assets/FX/generated/noise_wisp.png"),
			EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
			EmissionRectExtents = new Vector2(_tileSize.X * 0.4f, 2f),
			GlobalPosition = worldPosition - new Vector2(0f, _tileSize.Y * 0.4f),
			Direction = new Vector2(0f, -1f),
			Spread = 18f,
			Gravity = new Vector2(0f, -18f),
			InitialVelocityMin = 4f,
			InitialVelocityMax = 10f,
			AngularVelocityMin = -12f,
			AngularVelocityMax = 12f,
			ScaleAmountMin = 0.03f,
			ScaleAmountMax = 0.06f,
			ColorRamp = smokeRamp,
		};
		parent.AddChild(smoke);

		_smokeEmitters[tile] = smoke;
		_smokeOrder.Add(tile);
	}
}
