using Godot;

// One combined CCTV-style status panel: HP and Power both use the same
// segmented-meter language (no more hand-drawn HealthBar sprite), plus an
// upgrades row that lights up as Sam collects ability pickups (dash, extra
// jump, ...), all framed together under one "OPERATOR STATUS" header.
public partial class SamHUD : CanvasLayer
{
	[Export] public Sam Player { get; set; }
	// One segment per hit point — the row grows as MaxHealth upgrades are
	// collected instead of stretching a fixed bar (see UpdateHealth).
	[Export] public Vector2 HealthSegmentSize = new Vector2(16f, 14f);
	[Export] public int PowerSegmentCount = 14;
	[Export] public Vector2 SegmentSize = new Vector2(9f, 14f);
	[Export] public float SegmentGap = 3f;
	[Export] public Color HealthLitColor = new Color(0.95f, 0.35f, 0.3f, 1f);
	[Export] public Color HealthLitColorLow = new Color(1f, 0.75f, 0.2f, 1f);
	[Export] public Color PowerLitColor = new Color(0.45f, 1f, 0.6f, 1f);
	[Export] public Color PowerLitColorLow = new Color(1f, 0.35f, 0.3f, 1f);
	[Export] public Color SegmentUnlitColor = new Color(0.1f, 0.2f, 0.15f, 0.85f);
	[Export] public Color UpgradeLitColor = new Color(0.45f, 1f, 0.6f, 1f);
	[Export] public Color UpgradeUnlitColor = new Color(0.35f, 0.4f, 0.38f, 0.5f);

	private HealthComponent _playerHealth;
	private DashComponent _dashComponent;
	private ExtraJumpComponent _extraJumpComponent;

	private Control _healthSegmentRoot;
	private Control _powerSegmentRoot;
	private ColorRect[] _healthSegments;
	private ColorRect[] _powerSegments;

	private Panel _dashSlot;
	private Label _dashSlotLabel;
	private Panel _extraJumpSlot;
	private Label _extraJumpSlotLabel;

	private bool _lowHealth;
	private bool _lowPower;
	private float _pulseTime;

	public override void _Ready()
	{
		_healthSegmentRoot = GetNode<Control>("Panel/HealthSegments");
		_powerSegmentRoot = GetNode<Control>("Panel/PowerSegments");
		_powerSegments = BuildSegments(_powerSegmentRoot, SegmentSize, PowerSegmentCount);

		_dashSlot = GetNode<Panel>("Panel/UpgradeSlots/DashSlot");
		_dashSlotLabel = _dashSlot.GetNode<Label>("Label");
		_extraJumpSlot = GetNode<Panel>("Panel/UpgradeSlots/ExtraJumpSlot");
		_extraJumpSlotLabel = _extraJumpSlot.GetNode<Label>("Label");

		ApplyCrtTheme();
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		if (settings != null) settings.SettingsChanged += ApplyCrtTheme;

		if (Player == null)
		{
			GD.PushWarning("SamHUD: Player not assigned — HUD will not update.");
			return;
		}

		_playerHealth = Player.GetNode<HealthComponent>("HealthComponent");
		_playerHealth.HealthChanged += OnHealthChanged;
		_playerHealth.Died += OnPlayerDied;
		UpdateHealth(_playerHealth.CurrentHealth, _playerHealth.MaxHealth);

		Player.PowerChanged += OnPowerChanged;
		UpdatePower(Player.CurrentPower, Player.MaxPower);

		_dashComponent = Player.GetNodeOrNull<DashComponent>("DashComponent");
		if (_dashComponent != null)
		{
			_dashComponent.DashUnlocked += () => SetUpgradeSlotState(_dashSlot, _dashSlotLabel, true);
			SetUpgradeSlotState(_dashSlot, _dashSlotLabel, _dashComponent.IsUnlocked);
		}

		_extraJumpComponent = Player.GetNodeOrNull<ExtraJumpComponent>("ExtraJumpComponent");
		if (_extraJumpComponent != null)
		{
			_extraJumpComponent.ExtraJumpsChanged += _ => SetUpgradeSlotState(_extraJumpSlot, _extraJumpSlotLabel, true);
			SetUpgradeSlotState(_extraJumpSlot, _extraJumpSlotLabel, _extraJumpComponent.IsUnlocked);
		}
	}

	public override void _Process(double delta)
	{
		if (!_lowHealth && !_lowPower)
		{
			return;
		}

		_pulseTime += (float)delta;
		float pulse = Mathf.Sin(_pulseTime * 6f) * 0.5f + 0.5f;
		float brightness = Mathf.Lerp(0.55f, 1.15f, pulse);
		Color pulseColor = new Color(brightness, brightness, brightness, 1f);

		if (_lowHealth)
		{
			_healthSegmentRoot.Modulate = pulseColor;
		}
		if (_lowPower)
		{
			_powerSegmentRoot.Modulate = pulseColor;
		}
	}

	private ColorRect[] BuildSegments(Control root, Vector2 segmentSize, int count)
	{
		var segments = new ColorRect[count];
		for (int i = 0; i < count; i++)
		{
			var segment = new ColorRect
			{
				Size = segmentSize,
				Position = new Vector2(i * (segmentSize.X + SegmentGap), 0f),
				Color = SegmentUnlitColor,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			root.AddChild(segment);
			segments[i] = segment;
		}
		return segments;
	}

	private void OnHealthChanged(int currentHealth, int maxHealth)
	{
		UpdateHealth(currentHealth, maxHealth);
	}

	private void OnPlayerDied()
	{
		UpdateHealth(0, _playerHealth?.MaxHealth ?? 1);
	}

	// One pip per hit point rather than a fixed-count bar rescaled to fit —
	// a MaxHealth upgrade visibly adds a new segment to the row instead of
	// just changing how "full" a fixed bar reads, which otherwise made
	// health upgrades invisible on the HUD.
	private int _builtHealthSegmentCount = -1;

	private void UpdateHealth(int currentHealth, int maxHealth)
	{
		if (maxHealth != _builtHealthSegmentCount)
		{
			foreach (ColorRect segment in _healthSegments ?? [])
			{
				segment.QueueFree();
			}
			_healthSegments = BuildSegments(_healthSegmentRoot, HealthSegmentSize, maxHealth);
			_builtHealthSegmentCount = maxHealth;
		}

		bool critical = maxHealth > 0 && currentHealth <= Mathf.Max(1, maxHealth / 4);
		Color lit = critical ? HealthLitColorLow : HealthLitColor;

		for (int i = 0; i < _healthSegments.Length; i++)
		{
			_healthSegments[i].Color = i < currentHealth ? lit : SegmentUnlitColor;
		}

		_lowHealth = critical;
		if (!_lowHealth)
		{
			_healthSegmentRoot.Modulate = Colors.White;
		}
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
			_powerSegments[i].Color = i < litCount ? lit : SegmentUnlitColor;
		}

		_lowPower = !canSprint;
		if (!_lowPower)
		{
			_powerSegmentRoot.Modulate = Colors.White;
		}
	}

	private void SetUpgradeSlotState(Panel slot, Label label, bool unlocked)
	{
		var style = (StyleBoxFlat)slot.GetThemeStylebox("panel").Duplicate();
		style.BgColor = unlocked ? new Color(UpgradeLitColor, 0.18f) : new Color(UpgradeUnlitColor, 0.1f);
		style.BorderColor = unlocked ? UpgradeLitColor : UpgradeUnlitColor;
		slot.AddThemeStyleboxOverride("panel", style);
		label.Modulate = unlocked ? Colors.White : new Color(1f, 1f, 1f, 0.35f);
	}

	// Matches the CRT tint color chosen in Settings — same theme applies to
	// the full-screen menu CRTOverlay (see Code/UI/CRTOverlay.gd) and this
	// small in-HUD overlay, so both read as the same monitor style.
	private void ApplyCrtTheme()
	{
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		if (settings == null) return;
		var crtRect = GetNodeOrNull<ColorRect>("Panel/CRTOverlay");
		if (crtRect?.Material is ShaderMaterial mat)
		{
			mat.SetShaderParameter("tint_color", settings.CrtThemeColors[settings.CrtThemeIndex]);
		}
	}
}
