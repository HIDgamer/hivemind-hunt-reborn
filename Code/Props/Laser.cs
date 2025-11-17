using Godot;
using System;

public partial class Laser : Node2D
{
	[Export] public float OnTime = 3.5f; // How long laser stays active
	[Export] public float OffTime = 4.0f; // How long laser stays inactive
	[Export] public int Damage = 20; // Damage dealt to player

	private AnimatedSprite2D _animatedSprite; // Laser sprite with animations
	private Area2D _damageArea; // Collision area for player detection
	private Light2D _glowLight; // Glow effect when active
	private CpuParticles2D _sparkParticles; // Spark particles when active
	private Timer _timer; // Timer for on/off cycling
	private AudioStreamPlayer2D _activationPlayer; // Sound when activating
	private AudioStreamPlayer2D _idlePlayer; // Looping idle sound when active
	private bool _isActive = false; // Whether laser is currently active
	private float _pulseTime = 0f; // Time for pulsing animation
	private const float PULSE_SPEED = 12f; // Speed of flicker effect
	private const float PULSE_AMPLITUDE = 0.2f; // Intensity of flicker

	public override void _Ready()
	{
		// Initialize node references
		_animatedSprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_damageArea = GetNode<Area2D>("Area2D");
		_glowLight = GetNode<Light2D>("GlowLight");
		_sparkParticles = GetNode<CpuParticles2D>("SparkParticles");
		_timer = GetNode<Timer>("Timer");
		_activationPlayer = GetNode<AudioStreamPlayer2D>("ActivationPlayer");
		_idlePlayer = GetNode<AudioStreamPlayer2D>("IdlePlayer");

		// Connect damage detection signal
		_damageArea.BodyEntered += OnBodyEntered;

		// Start in inactive state
		SetLaserState(false);

		// Begin cycling timer
		_timer.WaitTime = OffTime;
		_timer.Start();
		_timer.Timeout += OnTimerTimeout;
	}

	public override void _Process(double delta)
	{
		if (_isActive)
		{
			// Create flickering light effect
			_pulseTime += (float)delta;
			float pulseFactor = 1f + Mathf.Sin(_pulseTime * PULSE_SPEED) * PULSE_AMPLITUDE;
			_glowLight.Energy = 1.5f * pulseFactor;

			// Deal continuous damage to players in range
			var bodies = _damageArea.GetOverlappingBodies();
			foreach (var body in bodies)
			{
				if (body is Sam player)
				{
					Vector2 knockbackDirection = (body.GlobalPosition - GlobalPosition).Normalized();
					player.TakeDamage(Damage, knockbackDirection);
					break; // Only damage one player at a time
				}
			}
		}
	}

	private void OnTimerTimeout()
	{
		if (_isActive)
		{
			// Deactivate laser
			SetLaserState(false);
			_animatedSprite.Play("Off");
			_timer.WaitTime = OffTime;
		}
		else
		{
			// Activate laser
			SetLaserState(true);
			_animatedSprite.Play("On");
			_timer.WaitTime = OnTime;

			// Play activation sound
			_activationPlayer.Stream = GD.Load<AudioStream>("res://Sound/effects/laser_point_defence_success.ogg");
			_activationPlayer.Play();
		}

		_timer.Start();
	}

	private void SetLaserState(bool active)
	{
		_isActive = active;
		_glowLight.Enabled = active; // Enable/disable glow effect
		_sparkParticles.Emitting = active; // Enable/disable spark particles

		if (active)
		{
			_animatedSprite.Play("Idle on");

			// Start idle sound loop
			if (!_idlePlayer.Playing)
			{
				_idlePlayer.Stream = GD.Load<AudioStream>("res://Sound/effects/laser-beam.mp3");
				_idlePlayer.Play();
			}
		}
		else
		{
			_animatedSprite.Play("Idle Off");

			// Stop idle sound
			_idlePlayer.Stop();
		}
	}

	// Called when animation finishes
	private void _on_AnimatedSprite2D_AnimationFinished()
	{
		if (_animatedSprite.Animation == "On")
		{
			_animatedSprite.Play("Idle on");
		}
		else if (_animatedSprite.Animation == "Off")
		{
			_animatedSprite.Play("Idle Off");
		}
	}

	// Called when player enters laser area
	private void OnBodyEntered(Node2D body)
	{
		if (_isActive && body is Sam player)
		{
			// Calculate knockback direction away from laser center
			Vector2 knockbackDirection = (body.GlobalPosition - GlobalPosition).Normalized();
			player.TakeDamage(Damage, knockbackDirection);
		}
	}
}