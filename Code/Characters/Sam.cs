using Godot;
using System.Collections.Generic;

public partial class Sam : CharacterBody2D
{
	// Dash trail tracking structure
	private struct DashTrailFrame
	{
		public Vector2 Position;
		public Texture2D Texture;
		public bool FlipH;
	}

	[ExportCategory("Networking")]
	// False (default) for the single-player Sam in Level_00_Tutorial — this
	// script otherwise behaves exactly as it always has. Only the
	// NetworkPlayer scene sets this true. When true and this peer isn't the
	// authority, all local input/physics/animation-state simulation is
	// skipped entirely — position and visual state instead arrive verbatim
	// from the authority via MultiplayerSynchronizer and are applied as-is.
	[Export] public bool IsNetworked { get; set; } = false;
	// Mirrors _animatedSprite.Animation/FlipH on the authority each frame;
	// a remote copy just plays back whatever arrives rather than re-running
	// the state machine, so it can never disagree with what the authority
	// actually decided to show.
	[Export] public string NetAnimationName { get; set; } = "Idle";
	[Export] public bool NetFlipH { get; set; } = false;
	[Export] public string DisplayName { get; set; } = "PLAYER";

	[ExportCategory("Movement Foundation")]
	[Export] public float Speed { get; set; } = 130f;
	[Export] public float WalkSpeed { get; set; } = 105f;
	[Export] public float SprintSpeed { get; set; } = 235f;
	[Export] public float CrawlSpeed { get; set; } = 55f;
	// Same technique as TurnaroundPlaybackSpeed — a speed multiplier on top
	// of the clip's own baked framerate, so the stand-to-prone windup doesn't
	// stall the player before Crawl/CrawlIdle takes over.
	[Export] public float CrawlEntryPlaybackSpeed { get; set; } = 1.8f;
	[Export] public float Acceleration { get; set; } = 1200f;
	[Export] public float AirAcceleration { get; set; } = 800f;
	[Export] public float Friction { get; set; } = 1400f;
	[Export] public float AirFriction { get; set; } = 240f;
	[Export] public float TurnAroundAccelMultiplier { get; set; } = 1.6f;
	// Minimum carried speed for a direction reversal to play the Turnaround/
	// TurnaroundRun flourish at all — a light tap of the opposite key while
	// nearly stopped shouldn't trigger a full pivot animation. Kept well
	// below WalkSpeed's own target (Speed) — a brief input gap
	// during a real key-swap (releasing one direction and pressing the
	// other is never perfectly instantaneous) lets friction decay velocity
	// for a frame or two, and at walking speed a 60+ threshold dropped
	// below that constantly, silently missing the reversal half the time.
	// Sprinting has far more headroom above either threshold, which is why
	// it read as far more reliable than walking before this was lowered.
	[Export] public float TurnaroundSpeedThreshold { get; set; } = 25f;
	// Hard cap on how long the Turnaround/TurnaroundRun lock can hold, in
	// case something interrupts the clip in a way that isn't one of the
	// explicit resets in HandleAnimations — should comfortably exceed the
	// clip's natural playback time so it never cuts a normal turn short.
	[Export] public float TurnaroundMaxDuration { get; set; } = 0.6f;
	// Movement never waits on this clip — it plays underneath full, immediate
	// input responsiveness (see Hollow Knight's turn for the reference feel).
	// 3.5x made a 9-frame clip strobe by at ~28ms/frame, reading as glitchy
	// rather than snappy — this is a more moderate speed-up that still
	// shortens the clip meaningfully without stepping through it too fast
	// to read as a pose.
	[Export] public float TurnaroundPlaybackSpeed { get; set; } = 1.8f;
	// The dash itself lasts 0.16s but the Flip clip runs ~1.3s at its baked
	// framerate — the flip keeps playing as follow-through after the dash
	// physics ends, and this speed brings the full somersault down to a
	// length that reads as part of the dash rather than dragging after it.
	[Export] public float FlipPlaybackSpeed { get; set; } = 2.0f;

	[ExportCategory("Verticality & Polish")]
	[Export] public float JumpVelocity { get; set; } = -315f;
	[Export] public float WallJumpVelocity { get; set; } = -305f;
	[Export] public float WallJumpHorizontalVelocity { get; set; } = 235f;
	[Export] public float Gravity { get; set; } = 780f;
	[Export] public float MaxFallSpeed { get; set; } = 390f;
	[Export] public float JumpCutMultiplier { get; set; } = 0.42f;
	[Export] public float ApexThreshold { get; set; } = 42f;
	[Export] public float ApexGravityMultiplier { get; set; } = 0.52f;
	[Export] public float WallSlideMaxFallSpeed { get; set; } = 90f;
	[Export] public float WallSlideGravityMultiplier { get; set; } = 0.2f;
	[Export] public float WallJumpControlLock { get; set; } = 0.06f;

	[ExportCategory("Forgiveness Mechanics")]
	[Export] public float CoyoteTime { get; set; } = 0.12f;
	[Export] public float JumpBufferTime { get; set; } = 0.12f;
	[Export] public int BaseJumpCount { get; set; } = 1;
	[Export] public float LandingParticleThreshold { get; set; } = 120f;
	[Export] public float DeathRespawnDelay { get; set; } = 1.5f;

	[ExportCategory("Crawl & Slide")]
	[Export] public float SlideEntrySpeed { get; set; } = 160f;
	[Export] public float SlideDuration { get; set; } = 1.5f;
	[Export] public float SlideFriction { get; set; } = 180f;
	// Same playback-multiplier treatment as Turnaround/CrawlEntry — every
	// windup clip gets one of these so none of them stalls the player at
	// its baked framerate.
	[Export] public float SlideInPlaybackSpeed { get; set; } = 1.8f;
	// The PushWindup clip's 9 windup frames run ~0.9s at their baked 10fps,
	// but InteractionComponent only holds the object still for
	// PushWindupDuration (~0.22s) — at 4x the art completes inside that
	// physics gate instead of getting chopped off partway through.
	[Export] public float PushWindupPlaybackSpeed { get; set; } = 4.0f;

	[ExportCategory("Power")]
	[Export] public float MaxPower { get; set; } = 100f;
	[Export] public float PowerRegenPerSecond { get; set; } = 28f;
	[Export] public float PowerRegenDelay { get; set; } = 0.65f;
	[Export] public float SprintPowerDrainPerSecond { get; set; } = 18f;
	[Export] public float MinimumPowerToSprint { get; set; } = 8f;

	[ExportCategory("Sound")]
	[Export] public AudioStream FootstepSound { get; set; }
	[Export] public AudioStream JumpSound { get; set; }
	[Export] public AudioStream LandSound { get; set; }
	[Export] public AudioStream[] HurtSounds { get; set; }
	[Export] public AudioStream DeathSound { get; set; }

	private AnimatedSprite2D _animatedSprite;
	private HealthComponent _health;
	private InteractionComponent _interaction;
	private DashComponent _dash;
	private ExtraJumpComponent _extraJump;
	private CollisionShape2D _normalCollision;
	private CollisionShape2D _crawlCollision; // "Crouch_Roll" node — low-profile shape for crawling
	private CpuParticles2D _jumpParticles;
	private CpuParticles2D _doubleJumpParticles;
	private CpuParticles2D _slideParticles;
	private CpuParticles2D _landParticles;
	private AudioStreamPlayer2D _footstepPlayer;
	private AudioStreamPlayer2D _jumpPlayer;
	private AudioStreamPlayer2D _landPlayer;
private AudioStreamPlayer2D _hurtPlayer;
private AudioStreamPlayer2D _interactionPlayer;
private CpuParticles2D _dashParticles;

	private enum State
	{
		Idle,
		Walk,
		Run,
		Jump,
		Fall,
		Float,
		Land,
		AirRoll,
		Slide,
		CrawlIdle,
		Crawl,
		Roll,
		WallSlide,
		Push,
		Pull,
		Hurt,
		Dead
	}

	private State _currentState = State.Idle;
	private float _coyoteTimer = 0f;
	private float _jumpBufferTimer = 0f;
	private float _stateLockTimer = 0f;
	private float _airRollTimer = 0f;
	private float _slideTimer = 0f;
	private float _footstepTimer = 0f;
	private float _movementSlowMultiplier = 1f;
	private float _movementSlowTimer = 0f;
	private float _powerRegenTimer = 0f;
	private int _extraJumpsUsed = 0;
	private int _extraJumpCapacity = 0;
	private bool _isCrawling = false;
	private bool _isSliding = false;
	private bool _isSprinting = false;
	private bool _wasInteracting = false;
	// SlideIn/CrawlEntry play once when their loop is freshly entered, then
	// hand off to the real loop once the windup's AnimationFinished fires —
	// see OnAnimationFinished. PushWindup instead follows InteractionComponent's
	// own physics-timed windup gate (IsWindingUpPush) since that one needs to
	// keep the animation and the "is the object actually allowed to move yet"
	// gate in exact agreement.
	private bool _slideWindupActive = false;
	private bool _crawlEntryActive = false;
	// Turnaround/TurnaroundRun have no separate left-facing art — the same
	// clip is played forward for one turn direction and backward (from its
	// last frame) for the other, since a time-reversed pivot reads as the
	// mirrored motion without needing a second drawn version.
	private bool _turnaroundActive = false;
	private float _turnaroundTimer = 0f;
	// Landing recovery plays until its clip finishes (or input cancels it) —
	// it used to be tied to the 0.04-0.08s landing state-lock timer, which
	// cut the 0.6s Land clip off after barely one frame, so it read as
	// never playing at all.
	private bool _landingAnimActive = false;
	// The dash flip's follow-through: stays true until the Flip clip
	// finishes, well after the dash physics itself (0.16s) has ended.
	private bool _flipAnimActive = false;
	private Tween _hurtFlashTween;

	// Animations with genuinely distinct left-facing art (not just a mirror
	// of the right-facing clip — asymmetric details like the ponytail don't
	// flip cleanly). For these, facing is expressed by swapping to the
	// "<Name>Left" clip instead of FlipH. Everything else still mirrors via
	// FlipH like before.
	private static readonly HashSet<string> DirectionalAnimations = new()
	{
		"Idle", "Walk", "Run", "Jump", "Land", "Hurt", "Death",
		"Pull", "PushWindup", "Push", "Slide", "SlideIn",
		"Crawl", "CrawlIdle", "CrawlEntry", "Flip", "Wallslide", "AirRoll",
	};
	private List<DashTrailFrame> _dashTrailFrames = new();
	private float _dashTrailTimer = 0f;
	private const float DashTrailInterval = 0.02f;
	private bool _isDashing = false;
	// Toggled by each wall jump so consecutive jumps off alternating walls
	// read as a deliberate zigzag climb: while active, horizontal input is
	// mirrored so pressing toward the wall you just left pushes off it
	// again instead of pinning you against it. See DoWallJump/HandleHorizontalMovement.
	private bool _wallClimbInputInverted = false;

	public float FacingDirection { get; private set; } = 1f;
	public float CurrentPower { get; private set; }
	public float PowerNormalized => MaxPower <= 0f ? 0f : CurrentPower / MaxPower;
	public bool IsAlive => _currentState != State.Dead;
	public bool IsInHurtState => _currentState == State.Hurt;
	public bool IsControlLocked => _stateLockTimer > 0f || _currentState == State.Hurt || _currentState == State.Dead || _interaction.IsInteracting;

	[Signal] public delegate void JumpedEventHandler(bool usedExtraJump);
	[Signal] public delegate void LandedEventHandler(float impactSpeed);
	[Signal] public delegate void PowerChangedEventHandler(float currentPower, float maxPower);

	public override void _Ready()
	{
		_animatedSprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_health = GetNode<HealthComponent>("HealthComponent");
		_interaction = GetNode<InteractionComponent>("InteractionComponent");
		_dash = GetNodeOrNull<DashComponent>("DashComponent");
		_extraJump = GetNodeOrNull<ExtraJumpComponent>("ExtraJumpComponent");
		_normalCollision = GetNodeOrNull<CollisionShape2D>("CollisionShape2D");
		_crawlCollision = GetNodeOrNull<CollisionShape2D>("Crouch_Roll");
		_jumpParticles = GetNodeOrNull<CpuParticles2D>("JumpParticles");
		_doubleJumpParticles = GetNodeOrNull<CpuParticles2D>("DoubleJumpParticles");
		_slideParticles = GetNodeOrNull<CpuParticles2D>("SlideParticles");
		_landParticles = GetNodeOrNull<CpuParticles2D>("LandParticles");
		_footstepPlayer = GetOrCreateAudioPlayer("FootstepPlayer");
		_jumpPlayer = GetOrCreateAudioPlayer("JumpPlayer");
		_landPlayer = GetOrCreateAudioPlayer("LandPlayer");
		_hurtPlayer = GetOrCreateAudioPlayer("HurtPlayer");
_interactionPlayer = GetOrCreateAudioPlayer("InteractPlayer");
_dashParticles = GetNodeOrNull<CpuParticles2D>("DashParticles");

	if (FootstepSound != null) _footstepPlayer.Stream = FootstepSound;
		if (JumpSound != null) _jumpPlayer.Stream = JumpSound;
		if (LandSound != null) _landPlayer.Stream = LandSound;

		CurrentPower = MaxPower;
		SetCrawlCollider(false);

		_animatedSprite.AnimationFinished += OnAnimationFinished;
		_health.TookDamage += OnTookDamage;
		_health.Died += OnDied;
		_extraJump?.ApplyToPlayer(this);

		// Only the local authority's own copy should have an active camera
		// or visible HUD — every other connected player's Sam is just a
		// remote puppet on screen, not something whose "point of view"
		// this client is watching from.
		if (IsNetworked && !IsMultiplayerAuthority())
		{
			GetNodeOrNull<Camera2D>("PlayerCamera")?.Set("enabled", false);
			GetNodeOrNull<CanvasLayer>("SamHUD")?.Set("visible", false);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		// Remote copy of another player: GlobalPosition, NetAnimationName,
		// and NetFlipH already arrived verbatim from the authority via
		// MultiplayerSynchronizer this tick — apply them and stop. No local
		// input reading, gravity, or state-machine simulation at all here,
		// so there's no way for a remote copy's own physics to disagree
		// with where the authority actually put it.
		if (IsNetworked && !IsMultiplayerAuthority())
		{
			ApplyRemoteVisualState();
			return;
		}

		if (_currentState == State.Dead) return;

		float deltaTime = (float)delta;
		Vector2 inputDir = new(Input.GetAxis("Left", "Right"), Input.GetAxis("Jump", "Crawl"));
		bool wasOnFloorBeforeMove = IsOnFloor();
		float fallSpeedBeforeMove = Velocity.Y;
		// Captured before HandleHorizontalMovement can react to this frame's
		// input — turnaround detection needs to know which way Sam was
		// actually facing/moving a moment ago, not the already-updated value.
		float facingBeforeMove = FacingDirection;
		float velocityXBeforeMove = Velocity.X;

		UpdateTimers(deltaTime);
		UpdateStatusEffects(deltaTime);
		UpdateInteraction(inputDir, deltaTime);
		UpdateDashTrail(deltaTime);

		bool dashActive = _dash != null && _dash.Tick(this, inputDir, deltaTime);
		if (dashActive)
		{
			if (!_isDashing) // Starting a new dash
			{
				_isDashing = true;
				_dashTrailFrames.Clear();
				_flipAnimActive = true;
				// A re-dash can land while the previous flip is still mid-air
				// in its follow-through — Stop() so Play() restarts the clip
				// from frame 0 instead of being skipped as "already playing".
				_animatedSprite.Stop();
			}
			StopSlide();
			SetState(State.Roll, "Flip", FlipPlaybackSpeed);
		}
		else if (_isDashing) // Dash just ended
		{
			_isDashing = false;
			// Spawn the dash clone effect now that the dash has finished
			SpawnDashEffect();
		}

		// Gravity keeps applying even while hurt-locked — only the
		// input-driven handlers below are gated off. This used to be
		// duplicated inside both branches below and skipped entirely during
		// Hurt, which meant getting knocked back mid-air froze Sam's fall
		// for the whole hurt-lock instead of letting the knockback arc
		// naturally: gravity would resume abruptly once the lock ended.
		if (!dashActive)
		{
			ApplyGravity(deltaTime);
		}

		if (!dashActive && _currentState != State.Hurt)
		{
			if (_interaction.IsInteracting)
			{
				HandleInteractionMovement(inputDir, deltaTime);
				HandleJumping(inputDir);
			}
			else
			{
				HandleCrawlAndSlide(inputDir, deltaTime);
				if (!_isSliding)
				{
					// Movement physics is never gated on the turnaround — see
					// StartTurnaround for why the clip itself is played fast
					// enough that this doesn't need to fight for sync.
					HandleHorizontalMovement(inputDir, deltaTime);
					HandleJumping(inputDir);
					HandleWallSlide(inputDir);
				}
			}
		}

		MoveAndSlide();
		HandleLanding(wasOnFloorBeforeMove, fallSpeedBeforeMove);
		UpdatePower(deltaTime);

		if (!dashActive && _currentState != State.Hurt && _currentState != State.Dead)
		{
			HandleAnimations(inputDir, facingBeforeMove, velocityXBeforeMove);
			HandleFootsteps(deltaTime);
		}

		if (IsNetworked && IsMultiplayerAuthority())
		{
			NetAnimationName = _animatedSprite.Animation;
			NetFlipH = _animatedSprite.FlipH;
		}
	}

	// A remote peer's copy never runs the state machine above — it just
	// shows whatever the authority's own sprite is actually doing.
	private void ApplyRemoteVisualState()
	{
		if (_animatedSprite.SpriteFrames != null
			&& _animatedSprite.SpriteFrames.HasAnimation(NetAnimationName)
			&& _animatedSprite.Animation != NetAnimationName)
		{
			_animatedSprite.Play(NetAnimationName);
		}
		_animatedSprite.FlipH = NetFlipH;
	}

	private void UpdateTimers(float deltaTime)
	{
		if (!IsOnFloor()) _coyoteTimer = Mathf.Max(0f, _coyoteTimer - deltaTime);
		if (Input.IsActionJustPressed("Jump")) _jumpBufferTimer = JumpBufferTime;
		else _jumpBufferTimer = Mathf.Max(0f, _jumpBufferTimer - deltaTime);

		_stateLockTimer = Mathf.Max(0f, _stateLockTimer - deltaTime);
		_airRollTimer = Mathf.Max(0f, _airRollTimer - deltaTime);

		if (_turnaroundActive)
		{
			_turnaroundTimer -= deltaTime;
			if (_turnaroundTimer <= 0f)
			{
				_turnaroundActive = false;
			}
		}
	}

	private void UpdateInteraction(Vector2 inputDir, float deltaTime)
	{
		if (Input.IsActionPressed("Interact"))
		{
			_interaction.ProcessInteraction(inputDir, GlobalPosition, deltaTime);
		}
		else if (_interaction.IsInteracting)
		{
			_interaction.StopInteraction();
		}

		if (_interaction.IsInteracting && !_wasInteracting)
		{
			StopSlide();
			SetCrawling(false);
			PlaySound(_interactionPlayer, 0.9f, 1.1f);
		}

		_wasInteracting = _interaction.IsInteracting;
	}

	private void ApplyGravity(float deltaTime)
	{
		if (IsOnFloor()) return;

		float currentGravity = Gravity;
		if (Mathf.Abs(Velocity.Y) < ApexThreshold && !Input.IsActionPressed("Crawl"))
		{
			currentGravity *= ApexGravityMultiplier;
		}

		if (IsWallSlideCandidate() && Velocity.Y > 0)
		{
			currentGravity *= WallSlideGravityMultiplier;
		}

		Vector2 newVelocity = Velocity;
		newVelocity.Y = Mathf.Min(newVelocity.Y + currentGravity * deltaTime, MaxFallSpeed);
		Velocity = newVelocity;
	}

	private void HandleHorizontalMovement(Vector2 inputDir, float deltaTime)
	{
		bool canSprint = CanSprint(inputDir);
		_isSprinting = canSprint;

		// When wallJumpInvertInput is active, invert the input direction so pressing
		// towards the wall pushes the player away, creating smooth back-and-forth wall climbing.
		float effectiveInputX = _wallClimbInputInverted ? -inputDir.X : inputDir.X;
		Vector2 effectiveInputDir = new(effectiveInputX, inputDir.Y);

		float targetSpeed = GetTargetHorizontalSpeed(effectiveInputDir, canSprint);
		float accel = IsOnFloor() ? Acceleration : AirAcceleration;
		float friction = IsOnFloor() ? Friction : AirFriction;

		Vector2 newVelocity = Velocity;
		if (Mathf.Abs(effectiveInputX) > 0.01f && _stateLockTimer <= 0f)
		{
			float directionMultiplier = Mathf.Sign(effectiveInputX);
			float speedDifference = Mathf.Abs(targetSpeed - newVelocity.X);
			float adjustedAccel = accel * (speedDifference > 50f ? 1.1f : 0.95f);

			// Snappier turn-around: if input is pressed against the current
			// direction of travel, boost acceleration so reversing reads as
			// an immediate, responsive stop-and-go instead of a sluggish
			// drift through zero.
			bool isReversing = Mathf.Sign(newVelocity.X) != 0f && Mathf.Sign(newVelocity.X) != directionMultiplier;
			if (isReversing)
			{
				adjustedAccel *= TurnAroundAccelMultiplier;
			}

			newVelocity.X = Mathf.MoveToward(newVelocity.X, targetSpeed, adjustedAccel * deltaTime);
			SetFacing(effectiveInputX);
		}
		else
		{
			newVelocity.X = Mathf.MoveToward(newVelocity.X, 0f, friction * deltaTime);
		}

		Velocity = newVelocity;

		// Reset wallJumpInvertInput when the player releases all horizontal input
		// and is no longer in contact with a wall
		if (_wallClimbInputInverted && !IsOnWallOnly() && Mathf.Abs(inputDir.X) < 0.01f)
		{
			_wallClimbInputInverted = false;
		}
	}

	private float GetTargetHorizontalSpeed(Vector2 inputDir, bool canSprint)
	{
		if (_isCrawling) return inputDir.X * CrawlSpeed * _movementSlowMultiplier;
		return inputDir.X * (canSprint ? SprintSpeed : Speed) * _movementSlowMultiplier;
	}

	private bool CanSprint(Vector2 inputDir)
	{
		return Input.IsActionPressed("Sprint")
			&& Mathf.Abs(inputDir.X) > 0.01f
			&& !_isCrawling
			&& !_isSliding
			&& CurrentPower > MinimumPowerToSprint;
	}

	private void HandleInteractionMovement(Vector2 inputDir, float deltaTime)
	{
		// Mirror the exact velocity InteractionComponent is driving into the
		// held object, rather than computing our own independent push/pull
		// speed here — two separately-tuned speeds fighting each other is
		// what made pushing/pulling feel like the player was bumping into
		// their own cargo instead of moving it smoothly.
		Vector2 newVelocity = Velocity;
		newVelocity.X = Mathf.MoveToward(newVelocity.X, _interaction.CurrentTargetVelocityX, Acceleration * deltaTime);
		Velocity = newVelocity;

		if (Mathf.Abs(inputDir.X) > 0.01f)
		{
			SetFacing(inputDir.X);
		}
	}

	private void HandleCrawlAndSlide(Vector2 inputDir, float deltaTime)
	{
		bool wantsCrawl = Input.IsActionPressed("Crawl") && IsOnFloor();
		bool canEnterSlide = Input.IsActionJustPressed("Crawl")
			&& IsOnFloor()
			&& !_isCrawling
			&& Mathf.Abs(Velocity.X) >= SlideEntrySpeed
			&& CurrentPower > MinimumPowerToSprint;

		if (canEnterSlide)
		{
			StartSlide(inputDir);
			return;
		}

		if (_isSliding)
		{
			TickSlide(deltaTime);
			return;
		}

		if (wantsCrawl)
		{
			SetCrawling(true);
		}
		else if (_isCrawling && CanStand())
		{
			SetCrawling(false);
		}
	}

	private void StartSlide(Vector2 inputDir)
	{
		_isSliding = true;
		_slideWindupActive = true;
		_slideTimer = SlideDuration;
		SetCrawling(true, playEntryAnimation: false);

		float direction = Mathf.Abs(inputDir.X) > 0.01f ? Mathf.Sign(inputDir.X) : FacingDirection;
		// Inherit current horizontal velocity, floored to SlideEntrySpeed so you always get a minimum slide boost
		float entrySpeed = Mathf.Max(Mathf.Abs(Velocity.X), SlideEntrySpeed);
		Velocity = new Vector2(direction * entrySpeed, Velocity.Y);
		SetFacing(direction);

		if (_slideParticles != null)
		{
			_slideParticles.Emitting = true;
		}
	}

	private void TickSlide(float deltaTime)
	{
		_slideTimer -= deltaTime;
		Vector2 newVelocity = Velocity;
		newVelocity.X = Mathf.MoveToward(newVelocity.X, 0f, SlideFriction * deltaTime);
		Velocity = newVelocity;

		if (_slideTimer <= 0f || Mathf.Abs(Velocity.X) < CrawlSpeed)
		{
			StopSlide();
		}
	}

	private void StopSlide()
	{
		if (!_isSliding) return;

		_isSliding = false;
		_slideWindupActive = false;
		if (_slideParticles != null)
		{
			_slideParticles.Emitting = false;
		}
	}

	private void HandleJumping(Vector2 inputDir)
	{
		if (Input.IsActionJustReleased("Jump") && Velocity.Y < 0f)
		{
			Velocity = new Vector2(Velocity.X, Velocity.Y * JumpCutMultiplier);
		}

		// Can't jump while crawling (prone) or sliding
		if (_isCrawling) return;

		if (_jumpBufferTimer <= 0f) return;

		if (_currentState == State.WallSlide || IsWallSlideCandidate())
		{
			DoWallJump();
			return;
		}

		bool canGroundJump = _coyoteTimer > 0f && BaseJumpCount > 0;
		bool canExtraJump = !canGroundJump && _extraJumpsUsed < _extraJumpCapacity;
		if (!canGroundJump && !canExtraJump) return;

		StopSlide();
		SetCrawling(false);

		Velocity = new Vector2(Velocity.X, JumpVelocity);
		if (canExtraJump)
		{
			_extraJumpsUsed++;
			_airRollTimer = 0.6f;
		}

		_coyoteTimer = 0f;
		_jumpBufferTimer = 0f;
		_stateLockTimer = 0f;
		EmitSignal(SignalName.Jumped, canExtraJump);
		PlayJumpFeedback(canExtraJump);
	}

	private void DoWallJump()
	{
		Vector2 wallNormal = GetWallNormal();
		float direction = wallNormal.X == 0f ? -FacingDirection : Mathf.Sign(wallNormal.X);
		Velocity = new Vector2(direction * WallJumpHorizontalVelocity, WallJumpVelocity);
		SetFacing(direction);

		_coyoteTimer = 0f;
		_jumpBufferTimer = 0f;
		_stateLockTimer = 0.05f;
		_airRollTimer = 0f;
		_wallClimbInputInverted = !_wallClimbInputInverted;
		PlayJumpFeedback(false);
	}

	private void HandleWallSlide(Vector2 inputDir)
{
	if (!IsWallSlideCandidate())
	{
		return;
	}

	StopSlide();
	SetCrawling(false);
	Vector2 newVelocity = Velocity;
	newVelocity.Y = Mathf.Min(newVelocity.Y, WallSlideMaxFallSpeed);
	Velocity = newVelocity;
	SetState(State.WallSlide, "Wallslide");
}

	private bool IsWallSlideCandidate()
	{
		return !IsOnFloor() && IsOnWallOnly() && Velocity.Y >= 0f;
	}

	private void HandleLanding(bool wasOnFloorBeforeMove, float fallSpeedBeforeMove)
	{
		if (!IsOnFloor()) return;

		_coyoteTimer = CoyoteTime;
		_extraJumpsUsed = 0;
		_extraJump?.ResetExtraJumps();

		if (!wasOnFloorBeforeMove)
		{
			if (fallSpeedBeforeMove >= LandingParticleThreshold)
			{
				PlayLandingFeedback(fallSpeedBeforeMove);
			}

			_landingAnimActive = true;
			_stateLockTimer = Mathf.Abs(Velocity.X) > 15f ? 0.04f : 0.08f;
			_wallClimbInputInverted = false;
		}
	}

	private void UpdatePower(float deltaTime)
	{
		bool drainedPower = false;
		if (_isSprinting)
		{
			SpendPower(SprintPowerDrainPerSecond * deltaTime);
			drainedPower = true;
		}

		if (drainedPower)
		{
			_powerRegenTimer = PowerRegenDelay;
			return;
		}

		if (_powerRegenTimer > 0f)
		{
			_powerRegenTimer -= deltaTime;
			return;
		}

		AddPower(PowerRegenPerSecond * deltaTime);
	}

	private void HandleAnimations(Vector2 inputDir, float facingBeforeMove, float velocityXBeforeMove)
	{
		if (_interaction.IsInteracting)
		{
			_turnaroundActive = false;
			_landingAnimActive = false;
			_flipAnimActive = false;
			HandleInteractionAnimation(inputDir);
			return;
		}

		if (_isSliding)
		{
			_turnaroundActive = false;
			_flipAnimActive = false;
			if (_slideWindupActive)
			{
				SetState(State.Slide, "SlideIn", SlideInPlaybackSpeed);
			}
			else
			{
				SetState(State.Slide, "Slide");
			}
			return;
		}

		if (IsWallSlideCandidate())
		{
			_turnaroundActive = false;
			_flipAnimActive = false;
			SetState(State.WallSlide, "Wallslide");
			return;
		}

		// Dash flip follow-through: the clip keeps playing to completion
		// (grounded or airborne) well after the 0.16s dash physics ended,
		// with movement fully responsive underneath — same philosophy as
		// Turnaround. Cleared by AnimationFinished or any branch above.
		if (_flipAnimActive)
		{
			SetState(State.Roll, "Flip", FlipPlaybackSpeed);
			return;
		}

if (!IsOnFloor())
{
	_turnaroundActive = false;
	_landingAnimActive = false;
	if (_airRollTimer > 0f)
	{
		SetState(State.AirRoll, "AirRoll");
	}
	else if (Mathf.Abs(Velocity.Y) < ApexThreshold)
	{
		// Apex/hang reads as a continuation of rising, not a separate pose —
		// only 2 real frames exist (jumping, falling).
		SetPinnedFrameState(State.Float, "Jump", 0);
	}
	else if (Velocity.Y < 0f)
	{
		SetPinnedFrameState(State.Jump, "Jump", 0);
	}
	else
	{
		_stateLockTimer = Mathf.Max(_stateLockTimer, 0.08f);
		SetPinnedFrameState(State.Fall, "Jump", 1);
	}
	return;
}

		// Landing recovery: plays out its full clip while standing still, but
		// any horizontal input cancels it instantly — landing into a run
		// keeps the run, only a standstill landing shows the recovery.
		if (_landingAnimActive)
		{
			if (Mathf.Abs(inputDir.X) > 0.01f)
			{
				_landingAnimActive = false;
			}
			else
			{
				_turnaroundActive = false;
				SetState(State.Land, "Land");
				return;
			}
		}

		if (_isCrawling)
		{
			bool isMoving = Mathf.Abs(inputDir.X) > 0.01f;
			State crawlState = isMoving ? State.Crawl : State.CrawlIdle;
			if (_crawlEntryActive)
			{
				SetState(crawlState, "CrawlEntry", CrawlEntryPlaybackSpeed);
			}
			else
			{
				SetState(crawlState, isMoving ? "Crawl" : "CrawlIdle");
			}
		}
		else if (Mathf.Abs(inputDir.X) > 0.01f)
		{
			float inputSign = Mathf.Sign(inputDir.X);
			if (!HandleTurnaround(facingBeforeMove, velocityXBeforeMove, inputSign))
			{
				SetState(_isSprinting ? State.Run : State.Walk, _isSprinting ? "Run" : "Walk");
			}
		}
		else
		{
			SetState(State.Idle, "Idle");
		}
	}

	// A direction reversal while still carrying real speed plays a brief
	// pivot clip instead of snapping straight into Walk/Run facing the new
	// way. This never touches Velocity or acceleration — it's a pure sprite
	// overlay on top of the existing (already-tuned) movement physics, so
	// input responsiveness is unaffected either way; only which frames are
	// shown changes. Returns true if a turnaround is starting or still
	// playing, so the caller should skip its own Walk/Run selection.
	private bool HandleTurnaround(float facingBeforeMove, float velocityXBeforeMove, float inputSign)
	{
		if (_turnaroundActive) return true;

		bool isReversal = facingBeforeMove != 0f
			&& inputSign == -facingBeforeMove
			&& Mathf.Sign(velocityXBeforeMove) == facingBeforeMove
			&& Mathf.Abs(velocityXBeforeMove) > TurnaroundSpeedThreshold;

		if (!isReversal) return false;

		// Tied to actual Sprint input/state rather than carried speed — a
		// reversal while genuinely sprinting plays TurnaroundRun, everything
		// else (including a fast walk) plays the plain Turnaround.
		string clip = _isSprinting ? "TurnaroundRun" : "Turnaround";

		// Turnaround/TurnaroundRun have no separate left-facing art — turning
		// to face left plays the clip forward, turning to face right plays it
		// backward from its last frame. Tune this if it reads mirrored from
		// what the art actually shows.
		bool playReversed = inputSign > 0f;
		StartTurnaround(clip, playReversed);
		return true;
	}

	private void StartTurnaround(string clip, bool playReversed)
	{
		_turnaroundActive = true;
		// Safety net: AnimationFinished is the normal way this clears, but
		// anything that interrupts the sprite mid-clip (jumping, landing,
		// sliding, grabbing an object — none of which wait on a cosmetic
		// pivot) would otherwise never fire it, permanently stuck-locking
		// Walk/Run out of HandleAnimations forever after. Every one of those
		// interrupt points also clears the flag directly, but this timeout
		// guarantees it can never wedge open even if some path doesn't.
		_turnaroundTimer = TurnaroundMaxDuration;
		_currentState = State.Walk;
		_animatedSprite.FlipH = false;
		if (playReversed)
		{
			_animatedSprite.Play(clip, -TurnaroundPlaybackSpeed, true);
		}
		else
		{
			_animatedSprite.Play(clip, TurnaroundPlaybackSpeed, false);
		}
	}

	private void HandleInteractionAnimation(Vector2 inputDir)
	{
		if (_interaction.CurrentPushable == null || Mathf.Abs(inputDir.X) < 0.01f)
		{
			SetState(State.Idle, "Idle");
			return;
		}

		if (_interaction.IsPushing)
		{
			if (_interaction.IsWindingUpPush)
			{
				SetState(State.Push, "PushWindup", PushWindupPlaybackSpeed);
			}
			else
			{
				SetState(State.Push, "Push");
			}
		}
		else
		{
			SetState(State.Pull, "Pull");
		}
	}

	private void HandleFootsteps(float deltaTime)
	{
		if (!IsOnFloor() || Mathf.Abs(Velocity.X) < 25f || _isSliding || _interaction.IsInteracting)
		{
			_footstepTimer = 0f;
			return;
		}

		float interval = _isSprinting ? 0.23f : _isCrawling ? 0.55f : 0.34f;
		_footstepTimer -= deltaTime;
		if (_footstepTimer <= 0f)
		{
			_footstepTimer = interval;
			PlaySound(_footstepPlayer, _isSprinting ? 1.05f : 0.82f, _isSprinting ? 1.22f : 1.0f);
		}
	}

	private void SetState(State state, string animation, float animationSpeed = 1.0f)
	{
		_currentState = state;
		PlayAnimation(animation, animationSpeed);
	}

	private void SetPinnedFrameState(State state, string animation, int frame)
	{
		_currentState = state;
		PlayPinnedFrame(animation, frame);
	}

	// Distinct left-facing clips (see DirectionalAnimations) replace FlipH
	// mirroring rather than combining with it — mirroring an already-correct
	// left-facing frame would flip it right back into looking wrong.
	private string ResolveDirectionalAnimation(string animation)
	{
		if (FacingDirection < 0f && DirectionalAnimations.Contains(animation))
		{
			string leftVariant = animation + "Left";
			if (_animatedSprite.SpriteFrames.HasAnimation(leftVariant))
			{
				return leftVariant;
			}
		}
		return animation;
	}

	private void PlayAnimation(string animation, float speed = 1.0f)
	{
		string resolved = ResolveDirectionalAnimation(animation);
		_animatedSprite.FlipH = FacingDirection < 0f && resolved == animation;

		// Also (re)plays when the sprite is stopped on this same clip — a
		// fresh dash Stop()s a still-running Flip so it restarts from frame
		// 0 here instead of being skipped as "already the current animation".
		bool needsPlay = _animatedSprite.Animation != resolved || !_animatedSprite.IsPlaying();
		if (_animatedSprite.SpriteFrames.HasAnimation(resolved) && needsPlay)
		{
			_animatedSprite.Play(resolved, speed);
		}
	}

	// Used for the unified 4-frame Jump clip: the frame is pinned directly
	// to the current physics phase (rise/apex/fall) instead of free-running
	// on the AnimatedSprite2D's own timer, so it stays in sync with actual
	// vertical velocity rather than looping independently of it.
	private void PlayPinnedFrame(string animation, int frame)
	{
		string resolved = ResolveDirectionalAnimation(animation);
		_animatedSprite.FlipH = FacingDirection < 0f && resolved == animation;

		if (!_animatedSprite.SpriteFrames.HasAnimation(resolved)) return;

		if (_animatedSprite.Animation != resolved)
		{
			_animatedSprite.Animation = resolved;
		}
		if (_animatedSprite.IsPlaying())
		{
			_animatedSprite.Pause();
		}
		int frameCount = _animatedSprite.SpriteFrames.GetFrameCount(resolved);
		_animatedSprite.Frame = Mathf.Clamp(frame, 0, frameCount - 1);
	}

	private void SetFacing(float direction)
	{
		if (Mathf.Abs(direction) < 0.01f) return;

		FacingDirection = Mathf.Sign(direction);
	}

	// playEntryAnimation is false wherever crawling starts as a continuation
	// of something that already reads as "low" — sliding, most notably —
	// where a stand-to-prone windup on top would look redundant rather than
	// like an actual transition.
	private void SetCrawling(bool crawling, bool playEntryAnimation = true)
	{
		if (_isCrawling == crawling) return;

		_isCrawling = crawling;
		if (crawling && playEntryAnimation) _crawlEntryActive = true;
		SetCrawlCollider(crawling);
	}

	private void SetCrawlCollider(bool crawling)
	{
		if (_normalCollision != null) _normalCollision.Disabled = crawling;
		if (_crawlCollision != null) _crawlCollision.Disabled = !crawling;
	}

	private bool CanStand()
	{
		if (_normalCollision == null || _crawlCollision == null) return true;

		bool normalWasDisabled = _normalCollision.Disabled;
		bool crawlWasDisabled = _crawlCollision.Disabled;
		_normalCollision.Disabled = false;
		_crawlCollision.Disabled = true;
		bool blocked = TestMove(GlobalTransform, new Vector2(0f, -0.5f));
		_normalCollision.Disabled = normalWasDisabled;
		_crawlCollision.Disabled = crawlWasDisabled;
		return !blocked;
	}

	private void UpdateStatusEffects(float deltaTime)
	{
		if (_movementSlowTimer <= 0f) return;

		_movementSlowTimer -= deltaTime;
		if (_movementSlowTimer <= 0f)
		{
			_movementSlowMultiplier = 1f;
		}
	}

	private void UpdateDashTrail(float deltaTime)
	{
		// Clear trail when dash is not active
		if (!_isDashing && _dashTrailFrames.Count > 0)
		{
			_dashTrailFrames.Clear();
		}

		if (!_isDashing) return;

		_dashTrailTimer -= deltaTime;
		if (_dashTrailTimer <= 0f)
		{
			_dashTrailTimer = DashTrailInterval;
			
			// Store current frame data with the actual texture
			if (_animatedSprite != null && _dashTrailFrames.Count < 25) // Max 25 frames to prevent memory issues
			{
				// Get the current frame's texture directly from SpriteFrames
				Texture2D frameTexture = null;
				if (_animatedSprite.SpriteFrames != null)
				{
					frameTexture = _animatedSprite.SpriteFrames.GetFrameTexture(_animatedSprite.Animation, _animatedSprite.Frame);
				}
				
				var frame = new DashTrailFrame
				{
					Position = GlobalPosition,
					Texture = frameTexture,
					FlipH = _animatedSprite.FlipH
				};
				_dashTrailFrames.Add(frame);
			}
		}
	}

	private void PlayJumpFeedback(bool usedExtraJump)
	{
		CpuParticles2D particles = usedExtraJump ? _doubleJumpParticles : _jumpParticles;
		if (particles != null)
		{
			particles.Emitting = false;
			particles.Restart();
			particles.Emitting = true;
		}

		PlaySound(_jumpPlayer, usedExtraJump ? 1.15f : 0.95f, usedExtraJump ? 1.35f : 1.1f);
	}

	private void PlayLandingFeedback(float impactSpeed)
	{
		if (_landParticles != null)
		{
			_landParticles.Emitting = false;
			_landParticles.Restart();
			_landParticles.Emitting = true;
		}

		float pitch = Mathf.Clamp(0.85f + impactSpeed / 700f, 0.85f, 1.25f);
		PlaySound(_landPlayer, pitch, pitch);
		EmitSignal(SignalName.Landed, impactSpeed);
	}

private AudioStreamPlayer2D GetOrCreateAudioPlayer(string nodeName)
{
	AudioStreamPlayer2D player = GetNodeOrNull<AudioStreamPlayer2D>(nodeName);
	if (player != null) return player;

	player = new AudioStreamPlayer2D { Name = nodeName };
	AddChild(player);
	return player;
}

private AudioStream GetRandomHurtSound()
{
	if (HurtSounds == null || HurtSounds.Length == 0) return null;
	int idx = (int)GD.RandRange(0, HurtSounds.Length - 1);
	return HurtSounds[idx];
}

	private void PlaySound(AudioStreamPlayer2D player, float minPitch = 1f, float maxPitch = 1f)
	{
		if (player?.Stream == null) return;

		player.PitchScale = Mathf.Lerp(minPitch, maxPitch, (float)GD.Randf());
		player.Play();
	}

	private void OnTookDamage(int amount, Vector2 knockbackDirection)
	{
		StopSlide();
		SetCrawling(false);
		// The Hurt clip replaces whatever was playing, so follow-through
		// flags waiting on AnimationFinished would wedge open without this.
		_flipAnimActive = false;
		_turnaroundActive = false;
		_landingAnimActive = false;
		_currentState = State.Hurt;
		Velocity = knockbackDirection * 100f;
		PlayAnimation("Hurt");
		FlashHurt();
		_hurtPlayer.Stream = GetRandomHurtSound();
		PlaySound(_hurtPlayer, 0.95f, 1.05f);

		SceneTreeTimer recoveryTimer = GetTree().CreateTimer(0.25f);
		recoveryTimer.Timeout += () =>
		{
			if (IsInstanceValid(this) && _currentState != State.Dead) _currentState = State.Idle;
		};
	}

	// The new Hurt sprite doesn't have a pain flash baked into its frames
	// the way the old one did, so the damage feedback is recreated here as
	// a quick red tint on the sprite itself, fading back to normal.
	private void FlashHurt()
	{
		if (_hurtFlashTween != null && _hurtFlashTween.IsValid())
		{
			_hurtFlashTween.Kill();
		}

		_animatedSprite.Modulate = new Color(1f, 0.35f, 0.35f, 1f);
		_hurtFlashTween = CreateTween();
		_hurtFlashTween.TweenProperty(_animatedSprite, "modulate", Colors.White, 0.3f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.Out);
	}

	// Chains the one-shot windup clips (SlideIn, CrawlEntry) into their
	// respective loops once the windup finishes playing, rather than
	// snapping straight into the loop the instant the state is entered.
	// Also clears the Turnaround/TurnaroundRun lock once that pivot clip
	// (played forward or backward — see StartTurnaround) finishes.
	private void OnAnimationFinished()
	{
		string finished = _animatedSprite.Animation;
		string baseName = finished.EndsWith("Left") ? finished[..^4] : finished;

		switch (baseName)
		{
			case "SlideIn":
				_slideWindupActive = false;
				break;
			case "CrawlEntry":
				_crawlEntryActive = false;
				break;
			case "Turnaround":
			case "TurnaroundRun":
				_turnaroundActive = false;
				break;
			case "Land":
				_landingAnimActive = false;
				break;
			case "Flip":
				_flipAnimActive = false;
				break;
		}
	}

private void OnDied()
{
	StopSlide();
	SetCrawling(false);
	_currentState = State.Dead;
	Velocity = Vector2.Zero;
	if (_hurtFlashTween != null && _hurtFlashTween.IsValid()) _hurtFlashTween.Kill();
	_animatedSprite.Modulate = Colors.White;
	PlayAnimation("Death");

	_hurtPlayer.Stream = DeathSound;
	PlaySound(_hurtPlayer, 0.95f, 1.05f);

	// Let the death animation/sound read, then respawn at the last
	// checkpoint (or reload the scene if none was reached yet) — dying
	// used to just leave Sam stuck in the Dead state forever with no
	// recovery path.
	SceneTreeTimer respawnTimer = GetTree().CreateTimer(DeathRespawnDelay);
	respawnTimer.Timeout += () =>
	{
		if (IsInstanceValid(this))
			GetNode<CheckpointManager>("/root/CheckpointManager").RespawnPlayer();
	};
}

// Pooled dash-ghost sprites — reused instead of allocated fresh every
// dash. A player spamming dash used to spawn up to 5 new Sprite2D nodes
// plus Tweens per dash with nothing capping how fast old ones cleared out.
private const int DashGhostPoolSize = 5;
private readonly List<Sprite2D> _dashGhostPool = new();
private readonly List<Tween> _dashGhostTweens = new();

private Sprite2D GetDashGhost(int index)
{
	while (_dashGhostPool.Count <= index)
	{
		var ghost = new Sprite2D { ZIndex = 10, Visible = false };
		GetTree().CurrentScene.AddChild(ghost);
		_dashGhostPool.Add(ghost);
		_dashGhostTweens.Add(null);
	}
	return _dashGhostPool[index];
}

private void FireDashGhost(int poolIndex, Texture2D texture, Vector2 globalPosition, bool flipH, float alpha, float scale, float fadeDuration)
{
	if (texture == null) return;

	Sprite2D ghost = GetDashGhost(poolIndex);
	Tween existingTween = _dashGhostTweens[poolIndex];
	if (existingTween != null && existingTween.IsValid())
		existingTween.Kill();

	ghost.Texture = texture;
	ghost.GlobalPosition = globalPosition;
	ghost.FlipH = flipH;
	ghost.Scale = Vector2.One * scale;
	ghost.Modulate = new Color(1, 1, 1, alpha);
	ghost.Visible = true;

	Tween tween = GetTree().CreateTween();
	_dashGhostTweens[poolIndex] = tween;
	tween.TweenProperty(ghost, "modulate:a", 0, fadeDuration);
	tween.TweenCallback(Callable.From(() =>
	{
		if (IsInstanceValid(ghost)) ghost.Visible = false;
	}));
}

public void SpawnDashEffect()
{
	if (_dashParticles != null)
	{
		// Exhaust kicks backward out of the dash, whichever way it went.
		_dashParticles.Direction = new Vector2(-FacingDirection, 0f);
		_dashParticles.Emitting = true;
		SceneTreeTimer particleTimer = GetTree().CreateTimer(0.35f);
		particleTimer.Timeout += () =>
		{
			if (IsInstanceValid(this) && _dashParticles != null) _dashParticles.Emitting = false;
		};
	}

	Texture2D fallbackTexture = null;
	if (_animatedSprite != null && _animatedSprite.SpriteFrames != null)
		fallbackTexture = _animatedSprite.SpriteFrames.GetFrameTexture(_animatedSprite.Animation, _animatedSprite.Frame);

	// Spawn up to 5 clones from the dash trail, evenly distributed
	int framesToSpawn = Mathf.Min(DashGhostPoolSize, _dashTrailFrames.Count);
	if (framesToSpawn <= 0)
	{
		// No trail frames yet (e.g., dash started immediately) — a single fallback ghost at the player's position.
		FireDashGhost(0, fallbackTexture, GlobalPosition, _animatedSprite?.FlipH ?? false, 0.8f, 0.96f, 0.35f);
		return;
	}

	for (int i = 0; i < framesToSpawn; i++)
	{
		// Select frames evenly distributed across the trail (oldest first, newest last)
		int frameIndex = (i * _dashTrailFrames.Count) / framesToSpawn;
		if (frameIndex >= _dashTrailFrames.Count) frameIndex = _dashTrailFrames.Count - 1;
		DashTrailFrame trailFrame = _dashTrailFrames[frameIndex];

		Texture2D texture = trailFrame.Texture != null ? trailFrame.Texture : fallbackTexture;
		float alpha = 0.8f - (i * 0.16f);
		float scale = 1f - i * 0.04f;
		float fadeDuration = 0.35f + (i * 0.1f);

		FireDashGhost(i, texture, trailFrame.Position, trailFrame.FlipH, alpha, scale, fadeDuration);
	}
}

	public void SetExtraJumpCapacity(int extraJumps)
	{
		_extraJumpCapacity = Mathf.Max(0, extraJumps);
		_extraJumpsUsed = Mathf.Min(_extraJumpsUsed, _extraJumpCapacity);
	}

	// Teleports to a checkpoint and resets everything death/falling would
	// otherwise leave broken: health, velocity, animation state, and the
	// timers that gate movement.
	public void RespawnAt(Vector2 position)
	{
		GlobalPosition = position;
		Velocity = Vector2.Zero;
		_currentState = State.Idle;
		_stateLockTimer = 0f;
		_coyoteTimer = 0f;
		_jumpBufferTimer = 0f;
		_isSliding = false;
		_slideWindupActive = false;
		_isCrawling = false;
		_crawlEntryActive = false;
		_turnaroundActive = false;
		_landingAnimActive = false;
		_flipAnimActive = false;
		_isDashing = false;
		_dashTrailFrames.Clear();
		SetCrawlCollider(false);
		if (_hurtFlashTween != null && _hurtFlashTween.IsValid()) _hurtFlashTween.Kill();
		_animatedSprite.Modulate = Colors.White;
		_health.Revive();
		PlayAnimation("Idle");
	}

	public void BeginAbilityLock(float duration)
	{
		_stateLockTimer = Mathf.Max(_stateLockTimer, duration);
	}

	public void ApplyMovementSlow(float multiplier, float duration)
	{
		_movementSlowMultiplier = Mathf.Clamp(multiplier, 0.1f, 1f);
		_movementSlowTimer = Mathf.Max(_movementSlowTimer, duration);
	}

	public bool TrySpendPower(float amount)
	{
		if (amount <= 0f) return true;
		if (CurrentPower < amount) return false;

		SpendPower(amount);
		_powerRegenTimer = PowerRegenDelay;
		return true;
	}

	private void SpendPower(float amount)
	{
		float previous = CurrentPower;
		CurrentPower = Mathf.Max(0f, CurrentPower - amount);
		if (!Mathf.IsEqualApprox(previous, CurrentPower))
		{
			EmitSignal(SignalName.PowerChanged, CurrentPower, MaxPower);
		}
	}

	private void AddPower(float amount)
	{
		float previous = CurrentPower;
		CurrentPower = Mathf.Min(MaxPower, CurrentPower + amount);
		if (!Mathf.IsEqualApprox(previous, CurrentPower))
		{
			EmitSignal(SignalName.PowerChanged, CurrentPower, MaxPower);
		}
	}
}
