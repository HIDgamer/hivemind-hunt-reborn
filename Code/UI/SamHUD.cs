using Godot;

// One combined CCTV-style status panel instead of two separately floating
// bars — same segmented-meter language for Power as before, the existing
// hand-drawn HealthBar sprite kept as-is (real authored art, not worth
// throwing away for a generated meter), both framed together under one
// "OPERATOR STATUS" header.
public partial class SamHUD : CanvasLayer
{
	[Export] public Sam Player { get; set; }
	[Export] public int PowerSegmentCount = 14;
	[Export] public Vector2 PowerSegmentSize = new Vector2(9f, 14f);
	[Export] public float PowerSegmentGap = 3f;
	[Export] public Color PowerLitColor = new Color(0.45f, 1f, 0.6f, 1f);
	[Export] public Color PowerLitColorLow = new Color(1f, 0.35f, 0.3f, 1f);
	[Export] public Color PowerUnlitColor = new Color(0.1f, 0.2f, 0.15f, 0.85f);

	private Sprite2D _healthBar;
	private HealthComponent _playerHealth;
	private Control _powerSegmentRoot;
	private ColorRect[] _powerSegments;
	private bool _lowPower;
	private float _pulseTime;

	public override void _Ready()
	{
		_healthBar = GetNode<Sprite2D>("Panel/HealthBar");
		_powerSegmentRoot = GetNode<Control>("Panel/PowerSegments");
		BuildPowerSegments();

		if (Player == null)
		{
			GD.PushWarning("SamHUD: Player not assigned — HUD will not update.");
			return;
		}

		_playerHealth = Player.GetNode<HealthComponent>("HealthComponent");
		_playerHealth.HealthChanged += OnHealthChanged;
		_playerHealth.Died += OnPlayerDied;
		UpdateHealthBar(_playerHealth.CurrentHealth, _playerHealth.MaxHealth);

		Player.PowerChanged += OnPowerChanged;
		UpdatePower(Player.CurrentPower, Player.MaxPower);
	}

	public override void _Process(double delta)
	{
		if (!_lowPower)
		{
			return;
		}

		_pulseTime += (float)delta;
		float pulse = Mathf.Sin(_pulseTime * 6f) * 0.5f + 0.5f;
		float brightness = Mathf.Lerp(0.55f, 1.15f, pulse);
		_powerSegmentRoot.Modulate = new Color(brightness, brightness, brightness, 1f);
	}

	private void BuildPowerSegments()
	{
		_powerSegments = new ColorRect[PowerSegmentCount];
		for (int i = 0; i < PowerSegmentCount; i++)
		{
			var segment = new ColorRect
			{
				Size = PowerSegmentSize,
				Position = new Vector2(i * (PowerSegmentSize.X + PowerSegmentGap), 0f),
				Color = PowerUnlitColor,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			_powerSegmentRoot.AddChild(segment);
			_powerSegments[i] = segment;
		}
	}

	private void OnHealthChanged(int currentHealth, int maxHealth)
	{
		UpdateHealthBar(currentHealth, maxHealth);
	}

	private void OnPlayerDied()
	{
		_healthBar.Frame = 10;
	}

	private void UpdateHealthBar(int currentHealth, int maxHealth)
	{
		int frame;
		if (currentHealth <= 0)
		{
			frame = 10;
		}
		else
		{
			// 10 segments (frames 0-9) span the full health pool, whatever
			// MaxHealth happens to be, rather than a hardcoded per-segment
			// amount that silently desyncs if MaxHealth ever changes.
			float segmentSize = maxHealth / 10f;
			frame = Mathf.FloorToInt((maxHealth - currentHealth) / segmentSize);
			frame = Mathf.Clamp(frame, 0, 9);
		}

		_healthBar.Frame = frame;
	}

	private void OnPowerChanged(float currentPower, float maxPower)
	{
		UpdatePower(currentPower, maxPower);
	}

	private void UpdatePower(float currentPower, float maxPower)
	{
		float normalized = maxPower > 0f ? Mathf.Clamp(currentPower / maxPower, 0f, 1f) : 0f;
		bool canSprint = Player != null && currentPower > Player.MinimumPowerToSprint;
		Color lit = canSprint ? PowerLitColor : PowerLitColorLow;

		int litCount = Mathf.RoundToInt(normalized * PowerSegmentCount);
		for (int i = 0; i < PowerSegmentCount; i++)
		{
			_powerSegments[i].Color = i < litCount ? lit : PowerUnlitColor;
		}

		_lowPower = !canSprint;
		if (!_lowPower)
		{
			_powerSegmentRoot.Modulate = Colors.White;
		}
	}
}
