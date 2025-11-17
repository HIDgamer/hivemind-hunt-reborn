using Godot;

/// <summary>
/// Periodically fires a fast comet-like streak across the space backdrop at
/// an unpredictable interval — a rare accent on top of the particle
/// starfield rather than something that reads as a repeating loop.
/// </summary>
public partial class ShootingStarSpawner : Node2D
{
	[Export] public float MinInterval = 4f;
	[Export] public float MaxInterval = 12f;
	[Export] public float Speed = 900f;
	[Export] public Vector2 SpawnAreaSize = new Vector2(1600f, 1000f);
	[Export] public Texture2D StreakTexture;
	// StreakTexture's bright streak runs vertically through the source
	// image (not horizontally), so it needs both a 90-degree rotation
	// offset (below) and a non-uniform scale — narrower on X than Y — to
	// read as an elongated comet trail rather than the full square image.
	[Export] public Vector2 StreakScale = new Vector2(0.12f, 0.45f);

	private readonly RandomNumberGenerator _rng = new();
	private double _timer;

	public override void _Ready()
	{
		_rng.Randomize();
		ScheduleNext();
	}

	public override void _Process(double delta)
	{
		_timer -= delta;
		if (_timer <= 0.0)
		{
			FireStreak();
			ScheduleNext();
		}
	}

	private void ScheduleNext()
	{
		_timer = _rng.RandfRange(MinInterval, MaxInterval);
	}

	private void FireStreak()
	{
		if (StreakTexture == null) return;

		var streak = new Sprite2D
		{
			Texture = StreakTexture,
			Modulate = new Color(1f, 1f, 1f, 0f),
			ZIndex = 5,
		};
		AddChild(streak);

		// Always a downward-diagonal arc, angle varies so it doesn't read
		// as the same shot repeating.
		float angle = _rng.RandfRange(Mathf.Pi * 0.15f, Mathf.Pi * 0.35f);
		Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

		Vector2 startPos = new Vector2(
			_rng.RandfRange(-SpawnAreaSize.X * 0.5f, SpawnAreaSize.X * 0.1f),
			_rng.RandfRange(-SpawnAreaSize.Y * 0.5f, -SpawnAreaSize.Y * 0.15f)
		);
		streak.Position = startPos;
		streak.Scale = StreakScale;
		streak.Rotation = direction.Angle() + Mathf.Pi / 2f;

		float travelDistance = SpawnAreaSize.Length();
		Vector2 endPos = startPos + direction * travelDistance;
		float duration = travelDistance / Speed;

		Tween tween = CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(streak, "position", endPos, duration)
			.SetTrans(Tween.TransitionType.Linear);
		tween.TweenProperty(streak, "modulate:a", 1.0, duration * 0.12)
			.SetTrans(Tween.TransitionType.Sine);
		tween.Chain().TweenInterval(duration * 0.55);
		tween.Chain().TweenProperty(streak, "modulate:a", 0.0, duration * 0.33)
			.SetTrans(Tween.TransitionType.Sine);
		tween.Chain().TweenCallback(Callable.From(() =>
		{
			if (IsInstanceValid(streak)) streak.QueueFree();
		}));
	}
}
