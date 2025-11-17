using Godot;

public partial class VentComponent : Area2D
{
	[Export] public NodePath TileMapPath { get; set; }
	[Export] public int PropLayerIndex { get; set; } = 1;
	[Export] public float VentAlpha { get; set; } = 0.3f;

	private TileMap _tileMap;
	private Color _originalModulate;
	private Color _ventModulate;

	public override void _Ready()
	{
		if (TileMapPath != null && !TileMapPath.IsEmpty)
		{
			_tileMap = GetNodeOrNull<TileMap>(TileMapPath);
		}

		if (_tileMap != null)
		{
			_originalModulate = _tileMap.GetLayerModulate(PropLayerIndex);
			_ventModulate = new Color(_originalModulate.R, _originalModulate.G, _originalModulate.B, VentAlpha);
		}

		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is Sam && _tileMap != null)
		{
			_tileMap.SetLayerModulate(PropLayerIndex, _ventModulate);
		}
	}

	private void OnBodyExited(Node2D body)
	{
		if (body is Sam && _tileMap != null)
		{
			_tileMap.SetLayerModulate(PropLayerIndex, _originalModulate);
		}
	}
}
