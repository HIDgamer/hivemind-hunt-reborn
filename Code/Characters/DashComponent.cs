using Godot;

public partial class DashComponent : Node
{
	[Export] public string DashAction { get; set; } = "Dash";
	[Export] public float DashSpeed { get; set; } = 420f;
	[Export] public float DashDuration { get; set; } = 0.16f;
	[Export] public float CooldownDuration { get; set; } = 0.45f;
	[Export] public float InvulnerabilityDuration { get; set; } = 0.18f;
	[Export] public bool ResetOnFloor { get; set; } = true;
	[Export] public bool UnlockedInitially { get; set; } = false;
	[Export] public float PowerCost { get; set; } = 35f;
	[Export] public AudioStream DashSound { get; set; }

	private float _dashTimer = 0f;
	private float _cooldownTimer = 0f;
	private Vector2 _dashDirection = Vector2.Right;
	private HealthComponent _health;
	private AudioStreamPlayer2D _audioPlayer;
	private bool _unlocked;

	public bool IsUnlocked => _unlocked;

	[Signal] public delegate void DashStartedEventHandler(Vector2 direction);
	[Signal] public delegate void DashFinishedEventHandler();
	[Signal] public delegate void DashUnlockedEventHandler();

	public override void _Ready()
	{
		_health = GetParent()?.GetNodeOrNull<HealthComponent>("HealthComponent");
		_unlocked = UnlockedInitially;
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");
		if (_audioPlayer == null)
		{
			_audioPlayer = new AudioStreamPlayer2D { Name = "AudioStreamPlayer2D", VolumeDb = -5f, Bus = "SFX" };
			AddChild(_audioPlayer);
		}

		if (DashSound != null)
		{
			_audioPlayer.Stream = DashSound;
		}
	}

	public bool Tick(Sam player, Vector2 inputDirection, float deltaTime)
	{
		if (player == null || !player.IsAlive || !_unlocked) return false;

		if (_cooldownTimer > 0f)
		{
			_cooldownTimer = Mathf.Max(0f, _cooldownTimer - deltaTime);
		}

		if (ResetOnFloor && player.IsOnFloor() && _dashTimer <= 0f)
		{
			_cooldownTimer = Mathf.Min(_cooldownTimer, CooldownDuration * 0.35f);
		}

		if (_dashTimer > 0f)
		{
			_dashTimer -= deltaTime;
			player.Velocity = _dashDirection * DashSpeed;

			if (_dashTimer <= 0f)
			{
				EmitSignal(SignalName.DashFinished);
			}

			return true;
		}

		if (!Input.IsActionJustPressed(DashAction) || _cooldownTimer > 0f || player.IsControlLocked || player.IsCrawling)
		{
			return false;
		}

		Vector2 direction = inputDirection;
		if (direction.LengthSquared() < 0.01f)
		{
			direction = new Vector2(player.FacingDirection, 0f);
		}

		if (!player.TrySpendPower(PowerCost))
		{
			return false;
		}

		StartDash(player, direction.Normalized());
		return true;
	}

	public void StartDash(Sam player, Vector2 direction)
	{
		if (player == null || direction == Vector2.Zero) return;

		_dashDirection = direction;
		_dashTimer = DashDuration;
		_cooldownTimer = CooldownDuration;
		player.BeginAbilityLock(DashDuration);
		player.Velocity = _dashDirection * DashSpeed;
		_health?.AddTemporaryInvulnerability(InvulnerabilityDuration);
		if (_audioPlayer?.Stream != null)
		{
			_audioPlayer.PitchScale = Mathf.Lerp(0.95f, 1.15f, (float)GD.Randf());
			_audioPlayer.Play();
		}
		EmitSignal(SignalName.DashStarted, _dashDirection);
	}

	public void UnlockDash()
	{
		if (_unlocked) return;

		_unlocked = true;
		EmitSignal(SignalName.DashUnlocked);
	}
}
