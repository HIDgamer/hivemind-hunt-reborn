using Godot;

// One combined CCTV-style status panel: a heartbeat icon + depleting health
// bar, a depleting stamina bar (color baked into its own sprite sheet), and
// an upgrades row that lights up with the matching icon as Sam collects
// ability pickups (dash, extra jump, ...), all framed together under one
// "OPERATOR STATUS" header.
//
// Health_Bar_Icon.png/Health_Bar.png/Stamina_Bar.png/Upgrades.png are all
// hand-drawn sprite sheets — sliced here via a single AtlasTexture per
// widget whose Region gets moved to the right frame, rather than pre-baking
// dozens of AtlasTexture sub-resources into the .tscn. AnimatedTexture would
// be the more obvious fit for the heartbeat loop, but Godot explicitly
// doesn't support AtlasTexture frames inside an AnimatedTexture — hand-
// rolling the frame advance in _Process is what actually lets a single
// sprite sheet work here, and it's also what makes "freeze on the last
// frame at 0 HP" trivial to control directly.
public partial class SamHUD : CanvasLayer
{
	[Export] public Sam Player { get; set; }

	[ExportGroup("Heartbeat")]
	// Frames-per-second the heart loops at, interpolated by HP fraction —
	// slow/calm at full health, franticly fast as HP drops, then frozen
	// entirely (see UpdateHealth) once dead.
	[Export] public float HeartbeatFpsFull = 1.6f;
	[Export] public float HeartbeatFpsCritical = 7f;

	private const int HeartFrameCount = 8;
	private const int HeartFrameSize = 32;
	private const int HealthBarFrameCount = 12;
	private const int HealthBarFrameWidth = 96;
	private const int HealthBarFrameHeight = 32;
	private const int StaminaBarFrameCount = 37;
	private const int StaminaBarFrameWidth = 80;
	private const int StaminaBarFrameHeight = 32;
	// Stamina_Bar_Icon.png shares the same 37-frame count as the bar itself
	// (unlike the heart, which loops independently of HP) — its frame index
	// is always kept in lockstep with the bar's own, not animated on its own
	// timer, so the badge's blue→yellow→red progression always matches
	// exactly what the bar is showing.
	private const int StaminaIconFrameSize = 32;
	private const int UpgradeIconSize = 48;
	// Upgrades.png's 4-icon strip order.
	private const int UpgradeIconGeneric = 0;
	private const int UpgradeIconHealth = 1;
	private const int UpgradeIconJump = 2;
	private const int UpgradeIconDash = 3;

	private HealthComponent _playerHealth;
	private DashComponent _dashComponent;
	private ExtraJumpComponent _extraJumpComponent;

	private TextureRect _healthIconRect;
	private AtlasTexture _healthIconAtlas;
	private float _heartFrameProgress;
	private int _heartFrame;
	private float _heartbeatFps = 1.6f;
	private bool _heartFrozen;

	private TextureRect _healthBarRect;
	private AtlasTexture _healthBarAtlas;
	private TextureRect _powerBarRect;
	private AtlasTexture _powerBarAtlas;
	private TextureRect _powerIconRect;
	private AtlasTexture _powerIconAtlas;

	private TextureRect _dashSlotIcon;
	private TextureRect _extraJumpSlotIcon;

	private Control _upgradeFlash;
	private TextureRect _upgradeFlashIcon;
	private Label _upgradeFlashLabel;
	private Tween _upgradeFlashTween;

	public override void _Ready()
	{
		Texture2D heartSheet = GD.Load<Texture2D>("res://Assets/UI/Health_Bar_Icon.png");
		Texture2D healthBarSheet = GD.Load<Texture2D>("res://Assets/UI/Health_Bar.png");
		Texture2D staminaBarSheet = GD.Load<Texture2D>("res://Assets/UI/Stamina_Bar.png");
		Texture2D staminaIconSheet = GD.Load<Texture2D>("res://Assets/UI/Stamina_Bar_Icon.png");
		Texture2D upgradesSheet = GD.Load<Texture2D>("res://Assets/UI/Upgrades.png");

		_healthIconRect = GetNode<TextureRect>("HealthIcon");
		_healthIconAtlas = new AtlasTexture { Atlas = heartSheet, Region = new Rect2(0, 0, HeartFrameSize, HeartFrameSize) };
		_healthIconRect.Texture = _healthIconAtlas;

		_healthBarRect = GetNode<TextureRect>("HealthBar");
		_healthBarAtlas = new AtlasTexture { Atlas = healthBarSheet, Region = new Rect2(0, 0, HealthBarFrameWidth, HealthBarFrameHeight) };
		_healthBarRect.Texture = _healthBarAtlas;

		_powerBarRect = GetNode<TextureRect>("PowerBar");
		_powerBarAtlas = new AtlasTexture { Atlas = staminaBarSheet, Region = new Rect2(0, 0, StaminaBarFrameWidth, StaminaBarFrameHeight) };
		_powerBarRect.Texture = _powerBarAtlas;

		_powerIconRect = GetNode<TextureRect>("PowerIcon");
		_powerIconAtlas = new AtlasTexture { Atlas = staminaIconSheet, Region = new Rect2(0, 0, StaminaIconFrameSize, StaminaIconFrameSize) };
		_powerIconRect.Texture = _powerIconAtlas;

		_dashSlotIcon = GetNode<TextureRect>("UpgradeSlots/DashSlot/Icon");
		_dashSlotIcon.Texture = MakeUpgradeIconAtlas(upgradesSheet, UpgradeIconDash);
		_extraJumpSlotIcon = GetNode<TextureRect>("UpgradeSlots/ExtraJumpSlot/Icon");
		_extraJumpSlotIcon.Texture = MakeUpgradeIconAtlas(upgradesSheet, UpgradeIconJump);

		_upgradeFlash = GetNode<Control>("UpgradeFlash");
		_upgradeFlashIcon = _upgradeFlash.GetNode<TextureRect>("Icon");
		_upgradeFlashLabel = _upgradeFlash.GetNode<Label>("Label");

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
			_dashComponent.DashUnlocked += () =>
			{
				SetUpgradeSlotState(_dashSlotIcon, true);
				FlashUpgradeAcquired("res://Assets/UI/Dash_Upgrade.png", "DASH UNLOCKED");
			};
			SetUpgradeSlotState(_dashSlotIcon, _dashComponent.IsUnlocked);
		}

		_extraJumpComponent = Player.GetNodeOrNull<ExtraJumpComponent>("ExtraJumpComponent");
		if (_extraJumpComponent != null)
		{
			_extraJumpComponent.ExtraJumpsChanged += _ =>
			{
				SetUpgradeSlotState(_extraJumpSlotIcon, true);
				FlashUpgradeAcquired("res://Assets/UI/Jump_Upgrade.png", "JUMP+ UNLOCKED");
			};
			SetUpgradeSlotState(_extraJumpSlotIcon, _extraJumpComponent.IsUnlocked);
		}
	}

	private static AtlasTexture MakeUpgradeIconAtlas(Texture2D sheet, int index)
	{
		return new AtlasTexture
		{
			Atlas = sheet,
			Region = new Rect2(index * UpgradeIconSize, 0, UpgradeIconSize, UpgradeIconSize),
		};
	}

	public override void _Process(double delta)
	{
		if (_heartFrozen) return;

		_heartFrameProgress += (float)delta * _heartbeatFps;
		if (_heartFrameProgress >= 1f)
		{
			_heartFrameProgress -= 1f;
			_heartFrame = (_heartFrame + 1) % HeartFrameCount;
			_healthIconAtlas.Region = new Rect2(_heartFrame * HeartFrameSize, 0, HeartFrameSize, HeartFrameSize);
		}
	}

	private void OnHealthChanged(int currentHealth, int maxHealth)
	{
		UpdateHealth(currentHealth, maxHealth);
	}

	private void OnPlayerDied()
	{
		UpdateHealth(0, _playerHealth?.MaxHealth ?? 1);
	}

	private void UpdateHealth(int currentHealth, int maxHealth)
	{
		float fraction = maxHealth > 0 ? Mathf.Clamp((float)currentHealth / maxHealth, 0f, 1f) : 0f;

		int barFrame = Mathf.RoundToInt((1f - fraction) * (HealthBarFrameCount - 1));
		_healthBarAtlas.Region = new Rect2(barFrame * HealthBarFrameWidth, 0, HealthBarFrameWidth, HealthBarFrameHeight);

		if (currentHealth <= 0)
		{
			// Dead — stop the heart cold on its last frame rather than
			// leaving it looping/twitching over a death screen.
			_heartFrozen = true;
			_heartFrame = HeartFrameCount - 1;
			_healthIconAtlas.Region = new Rect2(_heartFrame * HeartFrameSize, 0, HeartFrameSize, HeartFrameSize);
		}
		else
		{
			_heartFrozen = false;
			_heartbeatFps = Mathf.Lerp(HeartbeatFpsFull, HeartbeatFpsCritical, 1f - fraction);
		}
	}

	private void OnPowerChanged(float currentPower, float maxPower)
	{
		UpdatePower(currentPower, maxPower);
	}

	private void UpdatePower(float currentPower, float maxPower)
	{
		float fraction = maxPower > 0f ? Mathf.Clamp(currentPower / maxPower, 0f, 1f) : 0f;
		int barFrame = Mathf.RoundToInt((1f - fraction) * (StaminaBarFrameCount - 1));
		_powerBarAtlas.Region = new Rect2(barFrame * StaminaBarFrameWidth, 0, StaminaBarFrameWidth, StaminaBarFrameHeight);
		_powerIconAtlas.Region = new Rect2(barFrame * StaminaIconFrameSize, 0, StaminaIconFrameSize, StaminaIconFrameSize);
	}

	// The slots are plain Containers now (the old boxed Panel frame around
	// each icon was dropped in the HUD reorg) — "lit" is conveyed purely by
	// the icon's own modulate rather than a background stylebox tint.
	private void SetUpgradeSlotState(TextureRect icon, bool unlocked)
	{
		icon.Modulate = unlocked ? Colors.White : new Color(1f, 1f, 1f, 0.35f);
	}

	// Brief "just picked this up" popup using the bigger individual upgrade
	// art (Dash_Upgrade.png/Jump_Upgrade.png) — separate from the small
	// persistent Upgrades.png strip icon in the slot itself, which just
	// stays lit from here on.
	private void FlashUpgradeAcquired(string iconPath, string label)
	{
		_upgradeFlashIcon.Texture = GD.Load<Texture2D>(iconPath);
		_upgradeFlashLabel.Text = label;

		if (_upgradeFlashTween != null && _upgradeFlashTween.IsValid()) _upgradeFlashTween.Kill();

		_upgradeFlash.Visible = true;
		_upgradeFlash.Modulate = new Color(1f, 1f, 1f, 0f);
		_upgradeFlash.Scale = new Vector2(0.7f, 0.7f);
		_upgradeFlash.PivotOffset = _upgradeFlash.Size / 2f;

		_upgradeFlashTween = CreateTween();
		_upgradeFlashTween.TweenProperty(_upgradeFlash, "modulate:a", 1f, 0.15f);
		_upgradeFlashTween.Parallel().TweenProperty(_upgradeFlash, "scale", Vector2.One, 0.15f)
			.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
		_upgradeFlashTween.TweenInterval(1.2f);
		_upgradeFlashTween.TweenProperty(_upgradeFlash, "modulate:a", 0f, 0.3f);
		_upgradeFlashTween.TweenCallback(Callable.From(() => _upgradeFlash.Visible = false));
	}

	// Matches the CRT tint color chosen in Settings — same theme applies to
	// the full-screen menu CRTOverlay (see Code/UI/CRTOverlay.gd) and this
	// small in-HUD overlay, so both read as the same monitor style.
	private void ApplyCrtTheme()
	{
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		if (settings == null) return;
		var crtRect = GetNodeOrNull<ColorRect>("CRTOverlay");
		if (crtRect?.Material is ShaderMaterial mat)
		{
			mat.SetShaderParameter("tint_color", settings.CrtThemeColors[settings.CrtThemeIndex]);
		}
	}
}
