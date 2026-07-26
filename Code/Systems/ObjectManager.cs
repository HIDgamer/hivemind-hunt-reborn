using Godot;
using System.Collections.Generic;

// Reads marker tiles on a dedicated TileMap layer and replaces each one with
// a real scene instance at boot, so powerups/props/hazards/enemies don't all
// have to be hand-dragged into every level's scene tree. A marker tile just
// carries a spawn_key string (custom data); ObjectManager looks that key up
// in Catalog, instances the matching PackedScene at the tile's world
// position, and erases the marker so it doesn't linger.
//
// Deliberately NOT folded into GasSimulation/EnvironmentDamage despite the
// shared TileMap-layer-by-name/Instance-singleton shape — this system has no
// per-tick simulation at all, it only ever runs once at _Ready.
public partial class ObjectManager : Node2D
{
	public static ObjectManager Instance { get; private set; }

	[Export] public NodePath TileMapPath = "../TileMap";
	[Export] public string ObjectLayerName = "Objects";
	// String custom data layer on the SAME TileSet tiles, same convention as
	// GasSimulation's "is_vent"/"pipe_leak" and EnvironmentDamage's
	// "destructible" — just the first String-typed one in this project
	// instead of bool.
	[Export] public string SpawnKeyDataName = "spawn_key";
	// Index-aligned parallel arrays (SpawnKeys[i] -> SpawnScenes[i]), same
	// pattern as EnvironmentDamage.DestructibleLayerNames/DestructibleTerrainSets
	// — deliberately NOT a Godot.Collections.Dictionary<string, PackedScene>
	// export. That was tried first and lost data across an editor restart;
	// it's also the only generic-Dictionary export anywhere in this codebase,
	// with no working precedent, on a project that otherwise always reaches
	// for parallel arrays or string/TileMap-layer lookups for exactly this
	// "map a name to a resource" need.
	[Export] public string[] SpawnKeys = [];
	[Export] public PackedScene[] SpawnScenes = [];

	private TileMap _tileMap;
	private int _objectLayerIndex;
	private Node2D _spawnedObjectsContainer;
	private readonly Dictionary<string, PackedScene> _catalog = new();

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
			GD.PushWarning($"ObjectManager: no TileMap found at '{TileMapPath}' — disabling.");
			SetProcess(false);
			return;
		}

		_objectLayerIndex = ResolveLayerByName(ObjectLayerName);
		BuildCatalog();
		SpawnAll();
	}

	private void BuildCatalog()
	{
		int count = Mathf.Min(SpawnKeys.Length, SpawnScenes.Length);
		if (SpawnKeys.Length != SpawnScenes.Length)
		{
			GD.PushWarning($"ObjectManager: SpawnKeys ({SpawnKeys.Length}) and SpawnScenes ({SpawnScenes.Length}) are different lengths — only the first {count} pairs will be used.");
		}

		for (int i = 0; i < count; i++)
		{
			string key = SpawnKeys[i];
			if (string.IsNullOrEmpty(key) || SpawnScenes[i] == null) continue;

			if (_catalog.ContainsKey(key))
			{
				GD.PushWarning($"ObjectManager: duplicate spawn_key '{key}' at SpawnKeys index {i} — keeping the first entry.");
				continue;
			}

			_catalog[key] = SpawnScenes[i];
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}

	private int ResolveLayerByName(string layerName)
	{
		int count = _tileMap.GetLayersCount();
		for (int i = 0; i < count; i++)
		{
			if (_tileMap.GetLayerName(i) == layerName) return i;
		}
		GD.PushWarning($"ObjectManager: no TileMap layer named '{layerName}' found — no objects will be spawned. Check ObjectLayerName.");
		return -1;
	}

	private void SpawnAll()
	{
		if (_objectLayerIndex < 0) return;

		_spawnedObjectsContainer = new Node2D { Name = "SpawnedObjects" };
		AddChild(_spawnedObjectsContainer);

		foreach (Vector2I tile in _tileMap.GetUsedCells(_objectLayerIndex))
		{
			TileData tileData = _tileMap.GetCellTileData(_objectLayerIndex, tile);
			if (tileData == null) continue;

			string key = tileData.GetCustomData(SpawnKeyDataName).AsString();
			if (string.IsNullOrEmpty(key)) continue;

			if (!_catalog.TryGetValue(key, out PackedScene packedScene) || packedScene == null)
			{
				GD.PushWarning($"ObjectManager: no catalog entry for spawn_key '{key}' at tile {tile} — leaving marker tile in place.");
				continue;
			}

			Node2D instance = packedScene.Instantiate<Node2D>();
			instance.GlobalPosition = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
			instance.Rotation = ResolveMarkerRotation(_tileMap.GetCellAlternativeTile(_objectLayerIndex, tile));
			_spawnedObjectsContainer.AddChild(instance);

			_tileMap.EraseCell(_objectLayerIndex, tile);
		}
	}

	// Godot's tile transform for a square tile is expressed as flip_h/flip_v/
	// transpose flags, not a raw angle — there's no arbitrary rotation, only
	// the 4 combinations the TileMap editor's own rotate-90 buttons produce:
	// none = 0°, FlipH+Transpose = 90° CW, FlipH+FlipV = 180°,
	// FlipV+Transpose = 270° CW. Any other combination (a pure mirror with no
	// transpose) isn't a rotation at all, so it's treated as 0°.
	//
	// Read straight off the cell's alternative-tile ID via
	// TileSetAtlasSource.TransformFlipH/TransformFlipV/TransformTranspose,
	// NOT TileData.FlipH/FlipV/Transpose. TileData's properties are flags
	// authored on the tile resource itself (used for terrain matching) —
	// they do NOT reflect the per-cell transform the TileMap editor's
	// rotate/flip buttons bake into the alternative-tile ID when painting.
	// Reading TileData was why placed markers always spawned unrotated no
	// matter how they were rotated on the tile layer.
	private static float ResolveMarkerRotation(int alternativeTile)
	{
		bool flipH = (alternativeTile & (int)TileSetAtlasSource.TransformFlipH) != 0;
		bool flipV = (alternativeTile & (int)TileSetAtlasSource.TransformFlipV) != 0;
		bool transpose = (alternativeTile & (int)TileSetAtlasSource.TransformTranspose) != 0;

		if (!flipH && !flipV && !transpose) return 0f;
		if (flipH && !flipV && transpose) return Mathf.Pi * 0.5f;
		if (flipH && flipV && !transpose) return Mathf.Pi;
		if (!flipH && flipV && transpose) return Mathf.Pi * 1.5f;
		return 0f;
	}
}
