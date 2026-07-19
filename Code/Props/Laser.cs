using Godot;
using System;

public partial class Laser : Node2D
{
	// ── Timing ────────────────────────────────────────────────────────────────
	[ExportGroup("Timing")]
	[Export] public float OnTime   = 3.5f;  // Active duration (seconds)
	[Export] public float OffTime  = 4.0f;  // Dormant duration (seconds)
	[Export] public float WarnTime = 0.55f; // Pre-fire telegraph blink duration

	// ── Damage ────────────────────────────────────────────────────────────────
	[ExportGroup("Damage")]
	// Two-shots a 3 HP player (3 - 2 = 1, second hit finishes them) — deliberately
	// harsh so tanking through the beam isn't a viable way to skip the section.
	[Export] public int    Damage         = 2;
	[Export] public double DamageCooldown = 0.15; // Seconds between hit ticks

	// ── Beam ──────────────────────────────────────────────────────────────────
	[ExportGroup("Beam")]
	[Export] public float CastSpeed    = 7000.0f; // Extension speed (px/sec)
	[Export] public float MaxDistance  = 1000.0f; // Maximum beam reach (px)
	[Export] public float StartDistance = 0.0f;   // Offset from emitter origin
	[Export] public float GrowthTime   = 0.22f;   // Tween duration for width snap-on
	[Export] public float MaxLineWidth = 15.0f;    // Fully charged beam width

	// ── Direction ─────────────────────────────────────────────────────────────
	[ExportGroup("Direction")]
	[Export] public bool    UseRotationAsDirection = false;
	[Export] public Vector2 LaserDirection = Vector2.Down;

	// ── Signals ───────────────────────────────────────────────────────────────
	[Signal] public delegate void LaserActivatedEventHandler();
	[Signal] public delegate void LaserDeactivatedEventHandler();
	[Signal] public delegate void LaserHitEventHandler(Node2D target);

	// ── Node references ───────────────────────────────────────────────────────
	private AnimatedSprite2D    _sprite;
	private Line2D              _beamLine;   // Outer shader beam
	private Line2D              _coreLine;   // Inner white-hot core (optional)
	private Line2D              _glowLine;   // Wide additive glow — renders behind beam, tracks same points
	private Light2D             _glowLight;  // Small fixed-size impact flash at beam tip
	private Light2D             _warnLight;  // Pre-fire telegraph light (optional)
	private CpuParticles2D      _sparks;
	private CpuParticles2D      _smoke;      // Smoke puff particles at impact
	private Timer               _timer;
	private AudioStreamPlayer2D _activationPlayer;
	private AudioStreamPlayer2D _idlePlayer;
	private RayCast2D           _ray;
	private Area2D              _beamArea;
	private CollisionShape2D    _beamCollision;
	private RectangleShape2D    _beamRect;
	private ShaderMaterial      _shader;

	// ── State machine ─────────────────────────────────────────────────────────
	private enum LaserState { Off, Warning, Active }
	private LaserState _state = LaserState.Off;

	private Tween  _growthTween;
	private Tween  _warnTween;
	private double _timeSinceLastDamage = 0.0;

	// ── Sound bank ────────────────────────────────────────────────────────────
	private static readonly string[] StartSounds =
	{
		"uid://ddh31lbk5sjtj", "uid://ddu6huiyjknes",
		"uid://cgelyq37fhfmq", "uid://bpnvnlenxgh6e",
		"uid://bpogoht7t7k8r", "uid://dyu66l0h4wnq",
		"uid://cnei5swr6hmqm", "uid://f3lb5210e7tf",
		"uid://blec840kenoov",
	};

	// ─────────────────────────────────────────────────────────────────────────
	// Lifecycle
	// ─────────────────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_sprite           = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_beamLine         = GetNode<Line2D>("Line2D");
		_coreLine         = GetNodeOrNull<Line2D>("CoreLine2D");        // Optional
		_glowLine         = GetNodeOrNull<Line2D>("GlowLine2D");        // Optional
		_glowLight        = GetNode<Light2D>("GlowLight");
		_warnLight        = GetNodeOrNull<Light2D>("WarnLight");        // Optional
		_sparks           = GetNode<CpuParticles2D>("SparkParticles");
		_smoke            = GetNodeOrNull<CpuParticles2D>("SmokeParticles"); // Optional
		_timer            = GetNode<Timer>("Timer");
		_activationPlayer = GetNode<AudioStreamPlayer2D>("ActivationPlayer");
		_idlePlayer       = GetNode<AudioStreamPlayer2D>("IdlePlayer");
		_ray              = GetNode<RayCast2D>("RayCast2D");
		_beamArea         = GetNode<Area2D>("BeamArea");
		_beamCollision    = GetNode<CollisionShape2D>("BeamArea/CollisionShape2D");
		_shader           = _beamLine.Material as ShaderMaterial;

		_sprite.AnimationFinished += OnAnimationFinished;

		SetupRaycast();
		SetupBeamVisuals();
		SetupParticles();

		_beamRect = _beamCollision.Shape as RectangleShape2D;
		if (_beamRect != null)
			_beamRect.Size = Vector2.Zero;

		_timer.WaitTime  = OffTime;
		_timer.Timeout  += OnTimerTimeout;
		// Cycle timing is server-authoritative in a networked session: each
		// peer's timer starts whenever ITS scene finished loading, so
		// independently-run cycles are permanently phase-shifted between
		// host and clients — a beam one player sees as safe is live on the
		// other's screen (and burns/damage judged against different beams).
		// Clients never tick their own cycle; the server mirrors every
		// transition via RemoteCycle below.
		if (!IsNetClient())
		{
			_timer.Start();
		}

		// Late joiners shouldn't have to wait for the next cycle edge to
		// agree with the server — hand each newly connected peer the current
		// state immediately. (With the deferred-join flow, a peer is already
		// in the scene by the time PeerConnected fires, so this lands.)
		if (IsNetServer())
		{
			Multiplayer.PeerConnected += OnPeerConnectedSnapshot;
			_snapshotSubscribed = true;
		}

		TransitionTo(LaserState.Off, instant: true);
	}

	private bool _snapshotSubscribed;

	public override void _ExitTree()
	{
		if (_snapshotSubscribed)
		{
			Multiplayer.PeerConnected -= OnPeerConnectedSnapshot;
			_snapshotSubscribed = false;
		}
	}

	private void OnPeerConnectedSnapshot(long peerId)
	{
		RpcId(peerId, MethodName.RemoteSnapshot, (int)_state);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteSnapshot(int state)
	{
		TransitionTo((LaserState)state, instant: state == (int)LaserState.Off);
	}

	// IsClientSession (not raw IsNetworked) is load-bearing here: this is
	// called from _Ready, which on a joining client runs BEFORE the deferred
	// connection opens. Checking IsNetworked alone read false there, so
	// client lasers started their own local cycle timers and permanently
	// disagreed with the host about which beams were live.
	private bool IsNetClient()
	{
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		return networkManager != null && networkManager.IsClientSession;
	}

	private bool IsNetServer()
	{
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		return networkManager != null && networkManager.IsServerSession;
	}

	public override void _PhysicsProcess(double delta)
	{
		bool active = _state == LaserState.Active;

		Vector2 castTarget = active ? BeamDir * MaxDistance : Vector2.Zero;
		_ray.TargetPosition = _ray.TargetPosition.MoveToward(castTarget, CastSpeed * (float)delta);

		if (active)
			_ray.ForceRaycastUpdate();

		Vector2 beamEnd   = _ray.TargetPosition;
		bool    colliding = _ray.IsColliding();
		if (colliding)
			beamEnd = ToLocal(_ray.GetCollisionPoint());

		_beamLine.SetPointPosition(1, beamEnd);
		_coreLine?.SetPointPosition(1, beamEnd);
		_glowLine?.SetPointPosition(1, beamEnd);

		// Sync shader charge_level to line width — keeps appearance tied to tween
		_shader?.SetShaderParameter("charge_level",
			MaxLineWidth > 0f ? _beamLine.Width / MaxLineWidth : 0f);

		UpdateCollision(beamEnd);
		UpdateImpactFX(beamEnd, colliding);

		_timeSinceLastDamage += delta;
		if (active)
		{
			CheckBeamDamage();
			CheckBeamBurn((float)delta);
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Public API
	// ─────────────────────────────────────────────────────────────────────────

	public void FireInDirection(Vector2 worldDirection)
	{
		LaserDirection = worldDirection.Normalized();
		SyncParticleDirection();
	}

	public void AimAt(Vector2 globalTarget)
	{
		FireInDirection(globalTarget - GlobalPosition);
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Initialisation helpers
	// ─────────────────────────────────────────────────────────────────────────

	private void SetupRaycast()
	{
		_ray.CollideWithAreas  = true;
		_ray.CollideWithBodies = true;
		_ray.TargetPosition    = Vector2.Zero;
		_ray.Enabled           = true;
	}

	private void SetupBeamVisuals()
	{
		// Force Godot to generate UV coordinates for the shader
		_beamLine.TextureMode = Line2D.LineTextureMode.Stretch;
		
		Vector2 origin = BeamDir * StartDistance;

		_beamLine.Width = 0f;
		_beamLine.Visible = false;
		_beamLine.ClearPoints();
		_beamLine.AddPoint(origin);
		_beamLine.AddPoint(origin);

		if (_glowLine != null)
		{
			_glowLine.TextureMode = Line2D.LineTextureMode.Stretch;
			_glowLine.Width = 0f;
			_glowLine.Visible = false;
			_glowLine.ClearPoints();
			_glowLine.AddPoint(origin);
			_glowLine.AddPoint(origin);
		}

		if (_coreLine != null)
		{
			_coreLine.TextureMode = Line2D.LineTextureMode.Stretch;
			_coreLine.Width = 0f;
			_coreLine.Visible = false;
			_coreLine.ClearPoints();
			_coreLine.AddPoint(origin);
			_coreLine.AddPoint(origin);
		}

		_shader?.SetShaderParameter("charge_level", 0f);
	}

	private void SetupParticles()
	{
		SyncParticleDirection();
	}

	// ─────────────────────────────────────────────────────────────────────────
	// State machine
	// ─────────────────────────────────────────────────────────────────────────

	private void TransitionTo(LaserState next, bool instant = false)
	{
		_state = next;
		switch (next)
		{
			case LaserState.Off:     EnterOff(instant);  break;
			case LaserState.Warning: EnterWarning();      break;
			case LaserState.Active:  EnterActive();       break;
		}
	}

	private void EnterOff(bool instant)
	{
		KillWarnTween();
		if (_warnLight != null) _warnLight.Enabled = false;

		BeamDisappear(instant);
		_sprite.Play("Idle Off");
		_idlePlayer?.Stop();
		EmitSignal(SignalName.LaserDeactivated);
	}

	private void EnterWarning()
	{
		if (_warnLight != null)
		{
			_warnLight.Enabled = true;
			KillWarnTween();
			_warnTween = CreateTween().SetLoops();
			_warnTween.TweenProperty(_warnLight, "energy", 3.2f, WarnTime * 0.28f)
					  .SetTrans(Tween.TransitionType.Sine);
			_warnTween.TweenProperty(_warnLight, "energy", 0.4f, WarnTime * 0.28f)
					  .SetTrans(Tween.TransitionType.Sine);
		}

		_sprite.Play("On");

		SceneTreeTimer warnTimer = GetTree().CreateTimer(WarnTime);
		warnTimer.Timeout += () =>
		{
			if (IsInstanceValid(this) && _state == LaserState.Warning)
				TransitionTo(LaserState.Active);
		};
	}

	private void EnterActive()
	{
		KillWarnTween();
		if (_warnLight != null) _warnLight.Enabled = false;

		_ray.TargetPosition = BeamDir * StartDistance;
		BeamAppear();

		_sprite.Play("Idle on");
		_idlePlayer?.Play();

		int idx = (int)GD.RandRange(0, StartSounds.Length - 1);
		PlaySoundIfExists(_activationPlayer, StartSounds[idx]);

		EmitSignal(SignalName.LaserActivated);
	}

	private void OnTimerTimeout()
	{
		bool turningOff = _state is LaserState.Active or LaserState.Warning;
		if (IsNetServer())
		{
			Rpc(MethodName.RemoteCycle, turningOff);
		}

		if (turningOff)
		{
			TransitionTo(LaserState.Off);
			_sprite.Play("Off");
			_timer.WaitTime = OffTime;
		}
		else
		{
			_timer.WaitTime = OnTime + WarnTime;
			TransitionTo(LaserState.Warning);
		}
		_timer.Start();
	}

	// Mirrors the server's cycle edges on clients. The Warning->Active step
	// still runs locally through EnterWarning's own WarnTime timer, so only
	// the two authoritative edges need the network.
	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteCycle(bool turningOff)
	{
		if (turningOff)
		{
			TransitionTo(LaserState.Off);
			_sprite.Play("Off");
		}
		else
		{
			TransitionTo(LaserState.Warning);
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Beam visuals
	// ─────────────────────────────────────────────────────────────────────────

	private void BeamAppear()
	{
		KillGrowthTween();
		_growthTween = CreateTween();

		_beamLine.Width = 0f;
		_beamLine.Visible = true;

		// Lightsaber snap-on: spring transition gives a fast surge with slight
		// overshoot, so the beam "snaps" on rather than gradually fading in.
		_growthTween
			.TweenProperty(_beamLine, "width", MaxLineWidth, GrowthTime)
			.SetTrans(Tween.TransitionType.Spring)
			.SetEase(Tween.EaseType.Out);

		if (_glowLine != null)
		{
			_glowLine.Width = 0f;
			_glowLine.Visible = true;
			// Glow is ~1.5x the beam width — a soft ambient bleed around the
			// beam, not a wide wash across the whole corridor. The falloff
			// comes from GlowLine2D's gradient texture (see laser.tscn),
			// not from sheer width, so this can stay modest.
			_growthTween.Parallel()
						.TweenProperty(_glowLine, "width", MaxLineWidth * 1.5f, GrowthTime)
						.SetTrans(Tween.TransitionType.Spring)
						.SetEase(Tween.EaseType.Out);
		}

		if (_coreLine != null)
		{
			_coreLine.Width = 0f;
			_coreLine.Visible = true;
			// Core is ~28% of outer beam width — white-hot centre streak
			_growthTween.Parallel()
						.TweenProperty(_coreLine, "width", MaxLineWidth * 0.28f, GrowthTime)
						.SetTrans(Tween.TransitionType.Spring)
						.SetEase(Tween.EaseType.Out);
		}
	}

	private void BeamDisappear(bool instant)
	{
		KillGrowthTween();

		if (instant)
		{
			_beamLine.Width = 0f;
			_beamLine.Visible = false;
			if (_glowLine != null) { _glowLine.Width = 0f; _glowLine.Visible = false; }
			if (_coreLine != null) { _coreLine.Width = 0f; _coreLine.Visible = false; }
			_shader?.SetShaderParameter("charge_level", 0f);
			return;
		}

		_growthTween = CreateTween();

		// Fast Expo collapse — beam cuts off quickly like a real laser shutting down
		_growthTween
			.TweenProperty(_beamLine, "width", 0f, GrowthTime * 0.7f)
			.SetTrans(Tween.TransitionType.Expo)
			.SetEase(Tween.EaseType.In);

		if (_glowLine != null)
		{
			_growthTween.Parallel()
						.TweenProperty(_glowLine, "width", 0f, GrowthTime * 0.7f)
						.SetTrans(Tween.TransitionType.Expo)
						.SetEase(Tween.EaseType.In);
		}

		if (_coreLine != null)
		{
			_growthTween.Parallel()
						.TweenProperty(_coreLine, "width", 0f, GrowthTime * 0.7f)
						.SetTrans(Tween.TransitionType.Expo)
						.SetEase(Tween.EaseType.In);
		}

		_growthTween.TweenCallback(Callable.From(() =>
		{
			_beamLine.Visible = false;
			if (_glowLine != null) _glowLine.Visible = false;
			if (_coreLine != null) _coreLine.Visible = false;
		}));
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Collision & impact FX
	// ─────────────────────────────────────────────────────────────────────────

	private void UpdateCollision(Vector2 beamEnd)
	{
		if (_beamRect == null) return;

		float length      = beamEnd.Length();
		bool  shouldEnable = _state == LaserState.Active && length > 1.0f;
		_beamCollision.Disabled = !shouldEnable;

		if (!shouldEnable) return;

		Vector2 dir = BeamDir;
		// Track the beam's actual current rendered width (which tweens in/out
		// over GrowthTime, see BeamAppear/BeamDisappear) rather than the fully
		// charged MaxLineWidth — using the fixed max meant the hitbox snapped
		// to full width instantly while the visible beam was still a thin
		// sliver mid-growth, hitting the player well outside the visible
		// beam. 1.6x (matching GlowLine2D's own 1.5x bleed) keeps the hitbox
		// tied to the visible glow instead of an arbitrary fixed pad.
		_beamRect.Size = new Vector2(_beamLine.Width * 1.6f, length);
		_beamCollision.Position = dir * (StartDistance + length * 0.5f);
		_beamCollision.Rotation = dir.Angle() - Mathf.Pi * 0.5f;
	}

	private void UpdateImpactFX(Vector2 beamEnd, bool colliding)
	{
		bool showImpact = _state == LaserState.Active && colliding;

		if (_sparks != null)
		{
			_sparks.Position = beamEnd;
			_sparks.Emitting = showImpact;
		}

		if (_smoke != null)
		{
			_smoke.Position = beamEnd;
			_smoke.Emitting = showImpact;
		}

		// GlowLight is a small fixed-size impact flash at the beam tip only.
		// Beam-length glow is handled by GlowLine2D which tracks the points automatically.
		if (_glowLight != null)
		{
			_glowLight.Position = beamEnd;
			_glowLight.Enabled  = showImpact;
		}
	}

	private void SyncParticleDirection()
	{
		Vector2 dir = BeamDir;
		Vector2 reflected = new Vector2(-dir.X, -dir.Y);

		if (_sparks != null)
			_sparks.Direction = reflected;

		if (_smoke != null)
		{
			// Smoke always drifts upward regardless of beam direction,
			// with a slight lean away from the beam for realism.
			_smoke.Direction = new Vector2(reflected.X * 0.3f, -1f).Normalized();
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Damage
	// ─────────────────────────────────────────────────────────────────────────

	private void CheckBeamDamage()
	{
		if (_timeSinceLastDamage < DamageCooldown) return;

		foreach (Node2D body in _beamArea.GetOverlappingBodies())
		{
			if (TryDamage(body))
			{
				_timeSinceLastDamage = 0.0;
				return;
			}
		}

		foreach (Area2D area in _beamArea.GetOverlappingAreas())
		{
			if (TryDamage(area))
			{
				_timeSinceLastDamage = 0.0;
				return;
			}
		}
	}

	// Runs every tick the beam is active, independent of DamageCooldown —
	// burning is a continuous accumulation toward BurnableComponent's own
	// threshold, not a discrete hit like player/enemy damage.
	private void CheckBeamBurn(float deltaTime)
	{
		foreach (Node2D body in _beamArea.GetOverlappingBodies())
		{
			body.GetNodeOrNull<BurnableComponent>("BurnableComponent")?.ApplyBurn(deltaTime);
		}
	}

	private bool TryDamage(Node2D target)
	{
		HealthComponent health = target.GetNodeOrNull<HealthComponent>("HealthComponent");

		if (health == null && target.GetParent() is Node2D parent)
			health = parent.GetNodeOrNull<HealthComponent>("HealthComponent");

		if (health == null) return false;

		Vector2 knockback = (target.GlobalPosition - GlobalPosition).Normalized();
		health.Damage(Damage, knockback);
		EmitSignal(SignalName.LaserHit, target);
		return true;
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Helpers
	// ─────────────────────────────────────────────────────────────────────────

	private Vector2 BeamDir =>
		UseRotationAsDirection
			? Vector2.Right.Rotated(GlobalRotation)
			: LaserDirection.Normalized();

	private void OnAnimationFinished()
	{
		string anim = _sprite.Animation;
		if (anim == "On")  _sprite.Play("Idle on");
		if (anim == "Off") _sprite.Play("Idle Off");
	}

	private void KillGrowthTween()
	{
		if (_growthTween?.IsValid() == true) _growthTween.Kill();
	}

	private void KillWarnTween()
	{
		if (_warnTween?.IsValid() == true) _warnTween.Kill();
	}

	private void PlaySoundIfExists(AudioStreamPlayer2D player, string path)
	{
		if (player == null || !ResourceLoader.Exists(path)) return;
		player.Stream = GD.Load<AudioStream>(path);
		player.Play();
	}
}
