using Godot;

// Tile-grid line-of-sight: darkens every tile the local player doesn't
// currently have an unobstructed line to, using the level's own "Solid"
// TileMap layer (the same layer physics collision already uses) as the wall
// data — no separate authoring needed. Fixes seeing straight through walls
// into other rooms/lower levels/space, which the existing CanvasModulate
// ambient-black + per-tile LightOccluder2D setup doesn't actually prevent on
// its own (those only affect how light spreads, not whether unlit-but-in-
// view geometry gets drawn at all).
//
// Visibility itself (the Bresenham trace) is computed identically regardless
// of backend, only when the local player crosses into a new tile — a ~23x23
// grid of cheap integer steps for the default radius, trivial even on old
// hardware. What differs is how the result gets drawn, picked via
// GameSettings.LineOfSightMode (or auto-detected):
//   Gpu — one full-viewport shader pass reading a small per-tile mask
//         texture. Cheapest in the common case, but depends on the driver
//         actually running a custom canvas_item shader correctly.
//   Cpu — plain immediate-mode DrawRect calls, no shader at all: a handful
//         of coarse strips covering whatever's outside the fine grid, plus
//         one small rect per dark tile inside it. More draw calls, but each
//         one is trivial, and it sidesteps shader support entirely — the
//         fallback for weak/flaky integrated GPUs.
// Purely a local rendering concern (what does MY camera show ME), so none of
// this is networked — every peer computes and draws only their own view.
public partial class LineOfSightSystem : Node2D
{
	public enum Backend { Auto, Gpu, Cpu }

	[Export] public NodePath TileMapPath = "../TileMap";
	// Index of the level's "Solid" TileMap layer (see Level_00_Tutorial.tscn:
	// layer_2/name = "Solid") — presence of a tile there means "wall" for
	// sight purposes, exactly like it already does for physics.
	[Export] public int SolidLayer = 2;
	[Export] public int SightRadiusTiles = 11;

	// Filter mode is generated into the source rather than fixed, since it
	// has to match LineOfSightQuality: Blocky wants nearest (a hard edge
	// exactly at tile boundaries, matching the original "tile perfect" ask),
	// Smooth/Smoothest want linear so the supersampled + blurred mask (see
	// BuildMaskFromVisibility) actually reads as a soft antialiased edge
	// instead of being re-sharpened back to blocky by nearest sampling.
	private static string BuildShaderSource(bool smooth)
	{
		string filterHint = smooth ? "filter_linear" : "filter_nearest";
		return $@"
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D mask_texture : {filterHint};
uniform vec2 sprite_world_size;
uniform vec2 grid_offset_world;
uniform float tile_size;
uniform float grid_span_tiles;

void fragment() {{
	vec2 local_world_offset = (UV - 0.5) * sprite_world_size;
	vec2 offset_from_grid_center = local_world_offset + grid_offset_world;
	vec2 tile_offset = offset_from_grid_center / tile_size;
	vec2 grid_uv = (tile_offset / grid_span_tiles) + 0.5;

	float darkness = 1.0;
	if (grid_uv.x >= 0.0 && grid_uv.x <= 1.0 && grid_uv.y >= 0.0 && grid_uv.y <= 1.0) {{
		darkness = texture(mask_texture, grid_uv).a;
	}}

	COLOR = vec4(0.0, 0.0, 0.0, darkness);
}}
";
	}

	private TileMap _tileMap;
	private Vector2I _tileSize;
	private Vector2I _lastPlayerTile = new(int.MinValue, int.MinValue);
	private int _gridSize;
	// Flat gridSize*gridSize — true where a clear line reaches that cell.
	// Shared by both backends; only the rendering of it differs.
	private bool[] _visible;
	// What's actually drawn each frame — eases toward _visible's 0/1 targets
	// instead of snapping straight to them, so a tile crossing (which
	// recomputes _visible instantly) reads as sight smoothly catching up
	// with newly-revealed space rather than it popping into existence.
	// Shared by both backends for the same reason _visible is.
	private float[] _displayedDarkness;
	// Which absolute tile _displayedDarkness's index 0 grid is currently
	// centered on — the grid re-centers on the player every tile crossing,
	// so without tracking this and re-aligning the buffer (see
	// ShiftDisplayedDarkness) the same array slot would silently start
	// referring to a different tile after every step, comparing each cell's
	// eased value against an unrelated target and reading as a hard pulse
	// instead of a smooth fade.
	private Vector2I _darknessGridCenter = new(int.MinValue, int.MinValue);
	private const float FadeSpeed = 7f; // higher = faster catch-up, not a duration
	private const float FadeEpsilon = 0.002f;
	private bool _fadeConverged = true;
	private Backend _backend;

	// Gpu backend — mask texture resolution is _gridSize * _supersample (1
	// texel/tile at Blocky, up to 4 texels/tile at Smoothest), then
	// box-blurred by _blurRadius texels before upload. Both driven by
	// GameSettings.LineOfSightQuality, resolved once alongside the backend.
	private Sprite2D _gpuOverlay;
	private ShaderMaterial _material;
	private Image _maskImage;
	private ImageTexture _maskTexture;
	private int _supersample = 2;
	private int _blurRadius = 1;
	private int _maskResolution;
	private GameSettings _settings;

	// Cpu backend — recomputed every frame (not just on tile change) since
	// the camera can move/zoom independently of the player crossing a tile.
	private Rect2 _cpuViewRect;
	private Rect2 _cpuGridRect;

	public override void _Ready()
	{
		_tileMap = GetNodeOrNull<TileMap>(TileMapPath);
		if (_tileMap == null)
		{
			GD.PushWarning($"LineOfSightSystem: no TileMap found at '{TileMapPath}' — disabling.");
			SetProcess(false);
			return;
		}

		_tileSize = _tileMap.TileSet.TileSize;
		_gridSize = SightRadiusTiles * 2 + 1;
		_visible = new bool[_gridSize * _gridSize];
		_displayedDarkness = new float[_gridSize * _gridSize];
		System.Array.Fill(_displayedDarkness, 1f); // starts fully dark, same as _visible's default

		_backend = ResolveBackend();
		if (_backend == Backend.Gpu)
		{
			SetupGpuBackend();
			// Smoothing quality (not the Gpu/Cpu backend choice itself — that
			// still needs a level reload, see ResolveBackend) can change
			// mid-session from the settings menu and used to silently do
			// nothing until the next level load, since the mask texture was
			// only ever sized once here.
			_settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
			if (_settings != null) _settings.SettingsChanged += OnQualitySettingChanged;
		}
	}

	public override void _ExitTree()
	{
		if (_settings != null) _settings.SettingsChanged -= OnQualitySettingChanged;
	}

	private void OnQualitySettingChanged()
	{
		int oldSupersample = _supersample;
		bool oldSmooth = oldSupersample > 1;
		ResolveQuality();
		if (_supersample == oldSupersample) return; // this setting didn't actually change

		_maskResolution = _gridSize * _supersample;
		_maskImage = Image.CreateEmpty(_maskResolution, _maskResolution, false, Image.Format.Rgba8);
		_maskTexture = ImageTexture.CreateFromImage(_maskImage);

		// filter_nearest vs filter_linear is baked into the shader source
		// text itself (see BuildShaderSource) — crossing the Blocky<->Smooth
		// boundary needs a recompiled shader, not just a new texture.
		bool newSmooth = _supersample > 1;
		if (newSmooth != oldSmooth)
		{
			_material.Shader = new Shader { Code = BuildShaderSource(newSmooth) };
		}
		_material.SetShaderParameter("mask_texture", _maskTexture);
		_material.SetShaderParameter("tile_size", (float)_tileSize.X);
		_material.SetShaderParameter("grid_span_tiles", (float)_gridSize);

		_fadeConverged = false; // forces one BuildMaskFromVisibility pass at the new resolution next frame
	}

	// Mode is resolved once at level load, not re-checked live — matches how
	// several other settings in this project already only take full effect
	// on the next scene/level load rather than swapping instantly mid-session.
	private Backend ResolveBackend()
	{
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		int mode = settings?.LineOfSightMode ?? 0;
		if (mode == 1) return Backend.Gpu;
		if (mode == 2) return Backend.Cpu;

		// Auto — a rough "is this a weak machine" heuristic, not a precise
		// science: a low core count or a known integrated/software-renderer
		// adapter name both lean toward the no-shader CPU path; everything
		// else gets the GPU shader path, which is cheaper in the common case.
		string adapter = RenderingServer.GetVideoAdapterName().ToLowerInvariant();
		bool weakAdapter = adapter.Contains("intel") || adapter.Contains("microsoft basic")
			|| adapter.Contains("llvmpipe") || adapter.Contains("software");
		bool weakCpu = OS.GetProcessorCount() <= 2;
		return (weakAdapter || weakCpu) ? Backend.Cpu : Backend.Gpu;
	}

	// 0=Blocky, 1=Smooth, 2=Smoothest — see field comments on _supersample/
	// _blurRadius above for what each actually changes.
	private void ResolveQuality()
	{
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		int quality = settings?.LineOfSightQuality ?? 1;
		switch (quality)
		{
			case 0: _supersample = 1; _blurRadius = 0; break;
			case 2: _supersample = 4; _blurRadius = 2; break;
			default: _supersample = 2; _blurRadius = 1; break;
		}
	}

	private void SetupGpuBackend()
	{
		ResolveQuality();
		_maskResolution = _gridSize * _supersample;
		_maskImage = Image.CreateEmpty(_maskResolution, _maskResolution, false, Image.Format.Rgba8);
		_maskTexture = ImageTexture.CreateFromImage(_maskImage);

		_material = new ShaderMaterial { Shader = new Shader { Code = BuildShaderSource(_supersample > 1) } };
		_material.SetShaderParameter("mask_texture", _maskTexture);
		_material.SetShaderParameter("tile_size", (float)_tileSize.X);
		_material.SetShaderParameter("grid_span_tiles", (float)_gridSize);

		// A 1x1 placeholder — its own content is irrelevant since the shader
		// overrides COLOR outright; it only exists so Sprite2D has a texture
		// to generate a quad from, stretched via Scale to whatever world
		// area needs covering each frame.
		var placeholder = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		placeholder.SetPixel(0, 0, Colors.White);

		_gpuOverlay = new Sprite2D
		{
			Texture = ImageTexture.CreateFromImage(placeholder),
			Centered = true,
			Material = _material,
			// Highest z_index so it draws over the TileMap/props/lights/
			// parallax background regardless of tree position.
			ZIndex = 4096,
			ZAsRelative = false,
		};
		AddChild(_gpuOverlay);
	}

	public override void _Process(double delta)
	{
		if (_tileMap == null) return;

		Sam player = GetLocalPlayer();
		if (player == null) return;

		Camera2D camera = player.GetNodeOrNull<Camera2D>("PlayerCamera");
		if (camera == null || !camera.IsCurrent()) return;

		Vector2 cameraCenter = camera.GetScreenCenterPosition();
		// 10% margin so the overlay's edge doesn't visibly clip into view
		// during camera smoothing lag.
		Vector2 visibleWorldSize = (Vector2)GetViewport().GetVisibleRect().Size * camera.Zoom * 1.1f;

		Vector2I playerTile = _tileMap.LocalToMap(_tileMap.ToLocal(player.GlobalPosition));
		Vector2 gridCenterWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(playerTile));

		if (_backend == Backend.Gpu)
		{
			_gpuOverlay.GlobalPosition = cameraCenter;
			_gpuOverlay.Scale = visibleWorldSize;
			_material.SetShaderParameter("sprite_world_size", visibleWorldSize);
			_material.SetShaderParameter("grid_offset_world", cameraCenter - gridCenterWorld);
		}
		else
		{
			_cpuViewRect = new Rect2(cameraCenter - visibleWorldSize * 0.5f, visibleWorldSize);
			Vector2 gridWorldExtent = new Vector2(_gridSize, _gridSize) * _tileSize.X;
			_cpuGridRect = new Rect2(gridCenterWorld - gridWorldExtent * 0.5f, gridWorldExtent);
			QueueRedraw();
		}

		if (playerTile != _lastPlayerTile)
		{
			_lastPlayerTile = playerTile;
			RecomputeVisibility(playerTile);
		}

		UpdateDarknessFade((float)delta);
	}

	// Runs every frame regardless of whether a tile-cross just happened —
	// _visible's targets can still be mid-fade from the last few crossings.
	// Skips the (relatively) expensive Gpu mask rebuild once every cell has
	// actually reached its target, so a player standing still costs nothing
	// beyond this cheap per-cell lerp.
	private void UpdateDarknessFade(float delta)
	{
		float t = 1f - Mathf.Exp(-FadeSpeed * delta);
		bool stillFading = false;
		for (int i = 0; i < _displayedDarkness.Length; i++)
		{
			float target = _visible[i] ? 0f : 1f;
			float current = _displayedDarkness[i];
			if (Mathf.Abs(target - current) <= FadeEpsilon)
			{
				_displayedDarkness[i] = target;
				continue;
			}
			_displayedDarkness[i] = Mathf.Lerp(current, target, t);
			stillFading = true;
		}

		if (_backend == Backend.Gpu && (stillFading || !_fadeConverged))
		{
			BuildMaskFromVisibility();
		}
		_fadeConverged = !stillFading;
	}

	public override void _Draw()
	{
		if (_backend != Backend.Cpu || _visible == null) return;

		// Coarse strips covering whatever the fine grid below doesn't reach
		// — always fully dark, just four cheap rects regardless of how much
		// screen they cover.
		Rect2 view = _cpuViewRect;
		Rect2 grid = _cpuGridRect;
		DrawRect(new Rect2(view.Position, new Vector2(view.Size.X, grid.Position.Y - view.Position.Y)), Colors.Black); // top
		DrawRect(new Rect2(new Vector2(view.Position.X, grid.End.Y), new Vector2(view.Size.X, view.End.Y - grid.End.Y)), Colors.Black); // bottom
		DrawRect(new Rect2(new Vector2(view.Position.X, grid.Position.Y), new Vector2(grid.Position.X - view.Position.X, grid.Size.Y)), Colors.Black); // left
		DrawRect(new Rect2(new Vector2(grid.End.X, grid.Position.Y), new Vector2(view.End.X - grid.End.X, grid.Size.Y)), Colors.Black); // right

		// Fine, tile-perfect grid — alpha eases with _displayedDarkness rather
		// than snapping fully opaque/transparent, so a cell just revealed (or
		// just lost) fades rather than popping.
		float tile = _tileSize.X;
		for (int gy = 0; gy < _gridSize; gy++)
		{
			for (int gx = 0; gx < _gridSize; gx++)
			{
				float alpha = _displayedDarkness[gy * _gridSize + gx];
				if (alpha <= FadeEpsilon) continue;
				Vector2 cellPos = grid.Position + new Vector2(gx * tile, gy * tile);
				DrawRect(new Rect2(cellPos, new Vector2(tile, tile)), new Color(0, 0, 0, alpha));
			}
		}
	}

	// Same "find my own authority Sam" idiom used throughout this codebase
	// (ChatBox.SetLocalInputCaptured, VoiceChatManager.GetLocalSam) — never
	// trusts IsMultiplayerAuthority() without first checking IsNetworked.
	private Sam GetLocalPlayer()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is Sam sam && (!sam.IsNetworked || sam.IsMultiplayerAuthority()))
				return sam;
		}
		return null;
	}

	private bool IsSolid(Vector2I tile) => _tileMap.GetCellSourceId(SolidLayer, tile) != -1;

	// Re-centering the grid on the player every tile crossing means index
	// (gx,gy) in _displayedDarkness stops referring to the same absolute
	// tile it did last frame. Slide the buffer's contents by the tile delta
	// so each slot keeps tracking the same tile it was already easing toward
	// — cells sliding in from off the old grid start at 1 (dark), matching
	// how a cell nothing has traced to yet should look.
	private void RealignDisplayedDarkness(Vector2I newCenter)
	{
		if (_darknessGridCenter.X == int.MinValue)
		{
			_darknessGridCenter = newCenter;
			return;
		}
		if (newCenter == _darknessGridCenter) return;

		Vector2I delta = newCenter - _darknessGridCenter;
		var shifted = new float[_displayedDarkness.Length];
		for (int ngy = 0; ngy < _gridSize; ngy++)
		{
			for (int ngx = 0; ngx < _gridSize; ngx++)
			{
				int ogx = ngx + delta.X;
				int ogy = ngy + delta.Y;
				shifted[ngy * _gridSize + ngx] = (ogx >= 0 && ogx < _gridSize && ogy >= 0 && ogy < _gridSize)
					? _displayedDarkness[ogy * _gridSize + ogx]
					: 1f;
			}
		}
		_displayedDarkness = shifted;
		_darknessGridCenter = newCenter;
	}

	private void RecomputeVisibility(Vector2I center)
	{
		RealignDisplayedDarkness(center);
		System.Array.Clear(_visible, 0, _visible.Length);

		int radiusSq = SightRadiusTiles * SightRadiusTiles;
		for (int gy = 0; gy < _gridSize; gy++)
		{
			for (int gx = 0; gx < _gridSize; gx++)
			{
				int dx = gx - SightRadiusTiles;
				int dy = gy - SightRadiusTiles;
				if (dx * dx + dy * dy > radiusSq) continue; // circular sight, not a square

				TraceLine(center, center + new Vector2I(dx, dy));
			}
		}
		// Mask/rect drawing no longer happens here — UpdateDarknessFade picks
		// up the new targets and eases the actually-displayed darkness toward
		// them over the next several frames instead of snapping immediately.
	}

	// Upsamples the coarse boolean visibility grid (nearest-neighbor, one
	// block per tile) into the higher-resolution mask texture, then
	// box-blurs it — turning the hard tile-by-tile "staircase" into a
	// genuinely soft, antialiased gradient at the sight boundary and along
	// wall edges. At _supersample=1/_blurRadius=0 (Blocky) this reduces
	// exactly to the original hard per-tile mask, just via a slightly more
	// roundabout path — not worth a separate code path for one quality tier.
	private void BuildMaskFromVisibility()
	{
		var darkness = new float[_maskResolution, _maskResolution];
		for (int y = 0; y < _maskResolution; y++)
		{
			int gy = y / _supersample;
			for (int x = 0; x < _maskResolution; x++)
			{
				int gx = x / _supersample;
				darkness[y, x] = _displayedDarkness[gy * _gridSize + gx];
			}
		}

		if (_blurRadius > 0)
		{
			darkness = BoxBlur(darkness, _maskResolution, _blurRadius);
		}

		for (int y = 0; y < _maskResolution; y++)
		{
			for (int x = 0; x < _maskResolution; x++)
			{
				_maskImage.SetPixel(x, y, new Color(0, 0, 0, darkness[y, x]));
			}
		}
		_maskTexture.Update(_maskImage);
	}

	private static float[,] BoxBlur(float[,] src, int size, int radius)
	{
		var dst = new float[size, size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float sum = 0f;
				int count = 0;
				for (int oy = -radius; oy <= radius; oy++)
				{
					int sy = y + oy;
					if (sy < 0 || sy >= size) continue;
					for (int ox = -radius; ox <= radius; ox++)
					{
						int sx = x + ox;
						if (sx < 0 || sx >= size) continue;
						sum += src[sy, sx];
						count++;
					}
				}
				dst[y, x] = sum / count;
			}
		}
		return dst;
	}

	// Walks a Bresenham line from the player's tile to target, revealing
	// every tile along the way up to and including the first solid tile
	// encountered — you can see the wall you're looking at, nothing behind it.
	private void TraceLine(Vector2I origin, Vector2I target)
	{
		int x0 = origin.X, y0 = origin.Y;
		int x1 = target.X, y1 = target.Y;
		int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
		int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
		int err = dx + dy;

		while (true)
		{
			var current = new Vector2I(x0, y0);
			Reveal(current, origin);

			if (current != origin && IsSolid(current)) break;
			if (x0 == x1 && y0 == y1) break;

			int e2 = 2 * err;
			if (e2 >= dy) { err += dy; x0 += sx; }
			if (e2 <= dx) { err += dx; y0 += sy; }
		}
	}

	private void Reveal(Vector2I tile, Vector2I center)
	{
		int gx = tile.X - center.X + SightRadiusTiles;
		int gy = tile.Y - center.Y + SightRadiusTiles;
		if (gx < 0 || gy < 0 || gx >= _gridSize || gy >= _gridSize) return;
		_visible[gy * _gridSize + gx] = true;
	}
}
