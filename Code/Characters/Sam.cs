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
	// Mirrors _animatedSprite.Animation/Frame/FlipH on the authority each
	// frame; a remote copy is a PAUSED puppet that shows exactly this clip +
	// frame index rather than re-running the state machine or even letting
	// the clip self-advance. Replicating the frame (not a "play" command) is
	// what keeps every animation trick in sync for free: pinned jump/fall
	// frames, sped-up windups, reversed turnarounds, and one-shot restarts
	// all reduce to "which frame is the authority showing right now".
	[Export] public string NetAnimationName { get; set; } = "Idle";
	[Export] public int NetFrame { get; set; } = 0;
	[Export] public bool NetFlipH { get; set; } = false;
	[Export] public string DisplayName { get; set; } = "PLAYER";
	// Set true/false by VoiceChatManager on the authority the instant it
	// starts/stops actually transmitting (PushToTalk held), replicated to
	// every puppet like NetAnimationName — NameTag reads this to show a
	// speaking icon next to this player's nametag.
	[Export] public bool NetIsSpeaking { get; set; } = false;

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
	// How recently the opposite direction must have actually been held for a
	// new reversal to count as a Turnaround trigger, rather than a stray tap
	// while basically stationary. This replaced a raw carried-speed check
	// (velocity magnitude > threshold): under fast alternating taps (spam
	// clicking) friction can decay velocity below any fixed speed threshold
	// within a frame or two of releasing a key, so gating on velocity missed
	// the reversal about half the time no matter how low the threshold was
	// set. Gating on recent held-input direction instead survives that gap
	// since it isn't affected by how fast Friction decays Velocity.
	[Export] public float TurnaroundInputGraceTime { get; set; } = 0.2f;
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

	// Proximity voice chat playback — see VoiceChatManager.cs. Lazily built on
	// the first received chunk (a puppet that's never spoken to shouldn't
	// spin up a generator), and torn down again after a period of silence so
	// a gap in speech rebuilds fresh rather than trying to reason about
	// stale generator state across it.
	private const float VoiceIdleTimeoutSeconds = 0.4f;
	private AudioStreamPlayer2D _voiceChatPlayer;
	private AudioStreamGenerator _voiceStream;
	private AudioStreamGeneratorPlayback _voicePlayback;
	private float _voiceIdleSecondsLeft;

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
	// Tracks the last direction that actually had input held, and how long
	// ago that was — independent of Velocity/Friction (see
	// TurnaroundInputGraceTime for why that matters).
	private float _lastHeldInputSign = 0f;
	private float _lastHeldInputAge = 999f;
	// Landing recovery plays until its clip finishes (or input cancels it) —
	// it used to be tied to the 0.04-0.08s landing state-lock timer, which
	// cut the 0.6s Land clip off after barely one frame, so it read as
	// never playing at all.
	private bool _landingAnimActive = false;
	// The dash flip's follow-through: stays true until the Flip clip
	// finishes, well after the dash physics itself (0.16s) has ended.
	private bool _flipAnimActive = false;
	private Tween _hurtFlashTween;
	private Tween _dashFlashTween;
	private static CanvasItemMaterial _dashGhostAdditiveMaterial;

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
	// Manual moving-platform-style ride-along for standing on another
	// player's head. Rapier2D (this project's physics backend) has no
	// one-way-collision support at all — confirmed missing when
	// Platform.tscn/moving_platform.tscn were built — so there's no engine
	// feature to lean on here; this tracks whichever Sam we're currently
	// floor-supported by and manually carries their frame-to-frame motion
	// the same way a real moving platform would, entirely independent of
	// whether that other Sam is a local co-op instance, a remote authority,
	// or a synced puppet — all three just mean "read GlobalPosition".
	private Sam _ridingOnPlayer;
	private Vector2 _ridingOnPlayerLastPosition;

	// Exposed so ApplyPlayerSeparation (see below) can tell a ride-along
	// pair apart from an ordinary side-by-side encounter and leave it alone.
	public Sam RidingOnPlayer => _ridingOnPlayer;

	// True while a UI element (dialogue box, chat input) owns the keyboard —
	// all gameplay input reads as neutral so typing "wasd" in chat doesn't
	// walk Sam off a ledge and Z advances dialogue instead of grabbing crates.
	public bool UiInputCaptured { get; set; } = false;

	public float FacingDirection { get; private set; } = 1f;
	public float CurrentPower { get; private set; }
	public float PowerNormalized => MaxPower <= 0f ? 0f : CurrentPower / MaxPower;
	public bool IsAlive => _currentState != State.Dead;
	public bool IsInHurtState => _currentState == State.Hurt;
	public bool IsControlLocked => _stateLockTimer > 0f || _currentState == State.Hurt || _currentState == State.Dead || _interaction.IsInteracting || UiInputCaptured;
	public bool IsCrawling => _isCrawling;

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
	_voiceChatPlayer = GetNode<AudioStreamPlayer2D>("VoiceChatPlayer");

	if (FootstepSound != null) _footstepPlayer.Stream = FootstepSound;
		if (JumpSound != null) _jumpPlayer.Stream = JumpSound;
		if (LandSound != null) _landPlayer.Stream = LandSound;

		CurrentPower = MaxPower;
		SetCrawlCollider(false);

		_animatedSprite.AnimationFinished += OnAnimationFinished;
		_health.TookDamage += OnTookDamage;
		_health.Died += OnDied;
		_extraJump?.ApplyToPlayer(this);
		GetNodeOrNull<SquadAbilityState>("/root/SquadAbilityState")?.ApplyAll(this);

		if (IsNetworked)
		{
			// PlayerSpawner.SetMultiplayerAuthority() only runs on the server,
			// at the moment it decides to spawn this node — that call is
			// local to the server's own process and never gets replicated,
			// so every OTHER peer's copy of this same node still defaults to
			// authority = 1 (the server) unless it independently reaches the
			// same conclusion itself. PlayerSpawner names each instance after
			// the owning peer's id (see SpawnPlayer), which — unlike
			// authority — *is* part of normal node replication, so every
			// peer can derive the same authority from it here. Without this,
			// a joining (non-host) client's own player never satisfies
			// IsMultiplayerAuthority() on their own machine: input/physics
			// stay gated off (see the early-return below), and their own
			// camera/HUD get disabled by the check that follows, believing
			// itself to be a remote puppet of someone else.
			if (int.TryParse(Name, out int ownerId))
			{
				SetMultiplayerAuthority(ownerId);
			}

			if (IsMultiplayerAuthority())
			{
				// Broadcast my own chosen name — I'm the authority over my
				// own DisplayName, so this replicates out to every other
				// peer's NameTag via MultiplayerSynchronizer automatically.
				var localManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
				if (localManager != null && !string.IsNullOrEmpty(localManager.LocalPlayerName))
				{
					DisplayName = localManager.LocalPlayerName;
				}
			}

			// Players collide with each other again — spawns are staggered
			// now (see PlayerSpawner.SpawnPlayer), so the same-spot-shove-
			// through-the-floor bug this bit was pulled for no longer
			// applies, and solid player-vs-player contact is wanted on
			// purpose (see _ApplyPlayerRiding below).
		}

		// Only the local authority's own copy should have an active camera
		// or visible HUD — every other connected player's Sam is just a
		// remote puppet on screen, not something whose "point of view"
		// this client is watching from. PlayerCamera's scene default is
		// disabled specifically so this is the only place that ever turns
		// one on.
		//
		// Two multiplayer-only wrinkles beyond the authority check:
		//  - The pre-placed single-player Sam still runs _Ready before
		//    PlayerSpawner QueueFrees it. If it claimed the camera, the
		//    viewport's current camera died with it at end of frame and
		//    rendering fell back to the world origin until something else
		//    claimed current (toggling the pause menu happened to do that,
		//    which is why pausing "fixed" it). It must never take the
		//    camera when a session is active or pending.
		//  - Enabling a Camera2D does NOT steal "current" from whichever
		//    camera briefly held it first — MakeCurrent() must be explicit.
		var sessionManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		bool doomedPreplacedCopy = !IsNetworked && sessionManager != null
			&& (sessionManager.IsNetworked || sessionManager.HasPendingJoin);
		bool shouldHaveCamera = (!IsNetworked || IsMultiplayerAuthority()) && !doomedPreplacedCopy;
		var playerCamera = GetNodeOrNull<Camera2D>("PlayerCamera");
		if (playerCamera != null)
		{
			playerCamera.Enabled = shouldHaveCamera;
			if (shouldHaveCamera)
			{
				playerCamera.MakeCurrent();
			}
		}
		if ((IsNetworked && !IsMultiplayerAuthority()) || doomedPreplacedCopy)
		{
			GetNodeOrNull<CanvasLayer>("SamHUD")?.Set("visible", false);
		}

		// A save loaded from the main menu's Load screen leaves a pending
		// respawn request on CheckpointManager for exactly this scene —
		// multiplayer sessions never load a save this way, so this is
		// single-player only.
		if (!IsNetworked)
		{
			var checkpointManager = GetNodeOrNull<CheckpointManager>("/root/CheckpointManager");
			if (checkpointManager != null
				&& checkpointManager.ConsumePendingRespawn(GetTree().CurrentScene.SceneFilePath, out Vector2 loadPosition))
			{
				RespawnAt(loadPosition);
			}
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
			TickVoicePlayback((float)delta);
			return;
		}

		if (_currentState == State.Dead) return;

		float deltaTime = (float)delta;
		Vector2 inputDir = UiInputCaptured
			? Vector2.Zero
			: new(Input.GetAxis("Left", "Right"), Input.GetAxis("Jump", "Crawl"));
		bool wasOnFloorBeforeMove = IsOnFloor();
		float fallSpeedBeforeMove = Velocity.Y;
		// Captured before HandleHorizontalMovement can react to this frame's
		// input — turnaround detection needs to know which way Sam was
		// actually facing/moving a moment ago, not the already-updated value.
		float facingBeforeMove = FacingDirection;
		float velocityXBeforeMove = Velocity.X;
		float heldInputSignBeforeMove = _lastHeldInputSign;
		float heldInputAgeBeforeMove = _lastHeldInputAge;
		if (Mathf.Abs(inputDir.X) > 0.01f)
		{
			_lastHeldInputSign = Mathf.Sign(inputDir.X);
			_lastHeldInputAge = 0f;
		}
		else
		{
			_lastHeldInputAge += deltaTime;
		}

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
				FlashDashStart();

				// Seed the trail with the exact start position/pose right now —
				// otherwise the first timer-driven capture in UpdateDashTrail
				// wouldn't land until DashTrailInterval had elapsed, which on a
				// short dash could skip the true starting point entirely and
				// make the trail look like it begins partway through instead of
				// reaching all the way back to where the dash actually began.
				CaptureDashTrailFrame();
				_dashTrailTimer = DashTrailInterval;
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
				// Pushing/pulling and jumping are mutually exclusive — bracing
				// against a crate isn't a stance you jump out of, and letting
				// jump fire mid-push let players skip the "windup" entirely by
				// hopping instead of committing to the push.
				HandleInteractionMovement(inputDir, deltaTime);
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

		ApplyPlayerSeparation();
		MoveAndSlide();
		UpdatePlayerRiding();
		HandleLanding(wasOnFloorBeforeMove, fallSpeedBeforeMove);
		UpdatePower(deltaTime);

		if (!dashActive && _currentState != State.Hurt && _currentState != State.Dead)
		{
			HandleAnimations(inputDir, facingBeforeMove, heldInputSignBeforeMove, heldInputAgeBeforeMove);
			HandleFootsteps(deltaTime);
		}

		if (IsNetworked && IsMultiplayerAuthority())
		{
			NetAnimationName = _animatedSprite.Animation;
			NetFrame = _animatedSprite.Frame;
			NetFlipH = _animatedSprite.FlipH;
		}
	}

	// A remote peer's copy never runs the state machine above — and never
	// even plays its own sprite. It is held paused and told exactly which
	// clip + frame the authority is showing this tick. Letting the remote
	// Play() on its own (the old approach) desynced everything that isn't a
	// plain 1x forward loop: pinned jump/fall frames played through as full
	// clips, sped-up windups ran at the wrong rate, reversed turnarounds
	// played forwards, and a repeated one-shot (re-dash) never restarted
	// because the clip name hadn't changed.
	private void ApplyRemoteVisualState()
	{
		if (_animatedSprite.SpriteFrames == null) return;

		if (_animatedSprite.IsPlaying())
		{
			_animatedSprite.Pause();
		}

		if (_animatedSprite.SpriteFrames.HasAnimation(NetAnimationName))
		{
			if (_animatedSprite.Animation != NetAnimationName)
			{
				_animatedSprite.Animation = NetAnimationName;
			}
			// Name and frame can arrive from different sync ticks — clamp so
			// a stale frame index from the previous (longer) clip can't point
			// past the end of the new one.
			int frameCount = _animatedSprite.SpriteFrames.GetFrameCount(NetAnimationName);
			int frame = Mathf.Clamp(NetFrame, 0, Mathf.Max(0, frameCount - 1));
			if (_animatedSprite.Frame != frame)
			{
				_animatedSprite.Frame = frame;
			}
		}
		_animatedSprite.FlipH = NetFlipH;

		// Continuous (non-one-shot) effects derive from the synced clip
		// instead of needing their own replication: slide dust runs exactly
		// while a Slide/SlideIn clip is showing.
		if (_slideParticles != null)
		{
			bool sliding = NetAnimationName.StartsWith("Slide");
			if (_slideParticles.Emitting != sliding)
			{
				_slideParticles.Emitting = sliding;
			}
		}
	}

	private void UpdateTimers(float deltaTime)
	{
		if (!IsOnFloor()) _coyoteTimer = Mathf.Max(0f, _coyoteTimer - deltaTime);
		if (!UiInputCaptured && Input.IsActionJustPressed("Jump")) _jumpBufferTimer = JumpBufferTime;
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
		if (!UiInputCaptured && Input.IsActionPressed("Interact"))
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

			// Grabbing an object while still carrying sprint-speed momentum
			// let that momentum bleed off gradually through the normal
			// MoveToward easing in HandleInteractionMovement — brief, but
			// enough of a window (~150ms from sprint to push speed) to
			// noticeably speed-boost a push every time, cheesable by
			// sprinting into a grab repeatedly. Push/pull speed is fixed
			// regardless of Sprint, so carried speed should never exceed it.
			float maxCarrySpeed = _interaction.PushSpeed;
			if (Mathf.Abs(Velocity.X) > maxCarrySpeed)
			{
				Velocity = new Vector2(Mathf.Sign(Velocity.X) * maxCarrySpeed, Velocity.Y);
			}
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

		float targetSpeed = GetTargetHorizontalSpeed(inputDir, canSprint);
		float accel = IsOnFloor() ? Acceleration : AirAcceleration;
		float friction = IsOnFloor() ? Friction : AirFriction;

		Vector2 newVelocity = Velocity;
		if (Mathf.Abs(inputDir.X) > 0.01f && _stateLockTimer <= 0f)
		{
			float directionMultiplier = Mathf.Sign(inputDir.X);
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
			SetFacing(inputDir.X);
		}
		else
		{
			newVelocity.X = Mathf.MoveToward(newVelocity.X, 0f, friction * deltaTime);
		}

		Velocity = newVelocity;
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
		// _isSprinting is only ever updated in HandleHorizontalMovement, which
		// doesn't run while interacting — left stale (true) from sprinting
		// right up to the grab, it would otherwise keep draining sprint power
		// and playing sprint-speed footsteps for the entire push/pull, even
		// though push/pull always move at their own fixed speed regardless of
		// Sprint. Interaction has no sprint concept, so force it off.
		_isSprinting = false;

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

	// Softens player-vs-player contact: a gentle outward velocity nudge
	// applied BEFORE MoveAndSlide, instead of dropping players from each
	// other's collision_mask entirely. That more literal reading of "soft
	// collision" was tried first and reverted — IsOnFloor() (and therefore
	// jump/coyote-time/acceleration/friction/wall-slide/footsteps/landing,
	// all of which key off it directly) has no path to ever read true
	// against another player without a real physics collision, so removing
	// the mask bit would have also silently broken standing on someone's
	// head (UpdatePlayerRiding below still depends on a real MoveAndSlide
	// collision to detect that) and made jumping off them impossible. This
	// keeps the mask, and therefore every one of those systems, completely
	// unchanged — the nudge below just means two players drift apart
	// smoothly well before they'd ever reach a hard stop, so the felt
	// "annoying/sticky" case (shoved to a dead stop, jittering in place)
	// rarely gets reached at all, while still fundamentally blocking full
	// overlap the same as before.
	private const float PlayerSeparationMinDistance = 20f;
	private const float PlayerSeparationPushSpeed = 60f;

	private void ApplyPlayerSeparation()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is not Sam other || other == this) continue;
			// A ride-along pair should stay tightly coupled, not pushed apart.
			if (other == _ridingOnPlayer || other.RidingOnPlayer == this) continue;

			Vector2 offset = GlobalPosition - other.GlobalPosition;
			float dist = offset.Length();
			if (dist <= 0.01f || dist >= PlayerSeparationMinDistance) continue;
			// A large vertical gap means one of us is airborne above/below
			// the other (about to land on their head, or just jumped off) —
			// that's the ride-along system's territory, not a side-by-side
			// crowding case this nudge is meant for.
			if (Mathf.Abs(offset.Y) > PlayerSeparationMinDistance * 0.6f) continue;

			float strength = 1f - (dist / PlayerSeparationMinDistance);
			Velocity += new Vector2(Mathf.Sign(offset.X) * PlayerSeparationPushSpeed * strength, 0f);
		}
	}

	// Moving-platform-style ride-along for standing on another player's
	// head — see the field comment on _ridingOnPlayer for why this is
	// hand-rolled instead of using engine one-way-collision support.
	private void UpdatePlayerRiding()
	{
		Sam standingOn = null;
		if (IsOnFloor())
		{
			int count = GetSlideCollisionCount();
			for (int i = 0; i < count; i++)
			{
				KinematicCollision2D collision = GetSlideCollision(i);
				if (collision.GetCollider() is Sam otherSam && otherSam != this)
				{
					standingOn = otherSam;
					break;
				}
			}
		}

		if (standingOn != _ridingOnPlayer)
		{
			_ridingOnPlayer = standingOn;
			// First frame of a new ride: just start tracking their position —
			// applying a delta against a stale reading here would teleport us
			// by however far they'd already moved before we landed on them.
			_ridingOnPlayerLastPosition = standingOn?.GlobalPosition ?? Vector2.Zero;
			return;
		}

		if (_ridingOnPlayer != null && IsInstanceValid(_ridingOnPlayer))
		{
			Vector2 delta = _ridingOnPlayer.GlobalPosition - _ridingOnPlayerLastPosition;
			if (delta != Vector2.Zero)
			{
				GlobalPosition += delta;
			}
			_ridingOnPlayerLastPosition = _ridingOnPlayer.GlobalPosition;
		}
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

	private void HandleAnimations(Vector2 inputDir, float facingBeforeMove, float heldInputSignBeforeMove, float heldInputAgeBeforeMove)
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
			// Same reversal-while-crawling pivot as standing/sprinting, just
			// gated off during the crawl-entry windup — pivoting mid-entry
			// would fight the windup clip for the same frames.
			if (!_crawlEntryActive && isMoving
				&& HandleTurnaround(facingBeforeMove, heldInputSignBeforeMove, heldInputAgeBeforeMove, Mathf.Sign(inputDir.X)))
			{
				return;
			}

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
			if (!HandleTurnaround(facingBeforeMove, heldInputSignBeforeMove, heldInputAgeBeforeMove, inputSign))
			{
				SetState(_isSprinting ? State.Run : State.Walk, _isSprinting ? "Run" : "Walk");
			}
		}
		else
		{
			SetState(State.Idle, "Idle");
		}
	}

	// A direction reversal while the opposite direction was genuinely held a
	// moment ago plays a brief pivot clip instead of snapping straight into
	// Walk/Run facing the new way. This never touches Velocity or
	// acceleration — it's a pure sprite overlay on top of the existing
	// (already-tuned) movement physics, so input responsiveness is
	// unaffected either way; only which frames are shown changes. Returns
	// true if a turnaround is starting or still playing, so the caller
	// should skip its own Walk/Run selection.
	private bool HandleTurnaround(float facingBeforeMove, float heldInputSignBeforeMove, float heldInputAgeBeforeMove, float inputSign)
	{
		if (_turnaroundActive) return true;

		bool isReversal = facingBeforeMove != 0f
			&& inputSign == -facingBeforeMove
			&& heldInputSignBeforeMove == facingBeforeMove
			&& heldInputAgeBeforeMove <= TurnaroundInputGraceTime;

		if (!isReversal) return false;

		// Tied to actual Sprint input/state rather than carried speed — a
		// reversal while genuinely sprinting plays TurnaroundRun, everything
		// else (including a fast walk) plays the plain Turnaround. Crawling
		// has its own single pivot clip regardless of speed, same as how
		// crawling has no separate sprint variant of Crawl itself.
		string clip = _isCrawling ? "CrawlTurnaround" : _isSprinting ? "TurnaroundRun" : "Turnaround";

		// Turnaround/TurnaroundRun/CrawlTurnaround have no separate
		// left-facing art — turning to face left plays the clip forward,
		// turning to face right plays it backward from its last frame. Tune
		// this if it reads mirrored from what the art actually shows.
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
			RemoteFootstep(_isSprinting);
			BroadcastEffect(MethodName.RemoteFootstep, _isSprinting);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteFootstep(bool sprinting)
	{
		PlaySound(_footstepPlayer, sprinting ? 1.05f : 0.82f, sprinting ? 1.22f : 1.0f);
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
			CaptureDashTrailFrame();
		}
	}

	private void CaptureDashTrailFrame()
	{
		if (_animatedSprite == null || _dashTrailFrames.Count >= 25) return; // Max 25 frames to prevent memory issues

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

	// One-shot action effects (particles + sounds) only ever fire inside
	// authority-only code paths — remote copies skip the whole state machine
	// — so each one is broadcast as a cosmetic RPC and replayed verbatim on
	// every other peer's copy of this player. RpcMode.Authority means only
	// this player's owner can trigger their effects remotely.
	private void BroadcastEffect(StringName method, params Variant[] args)
	{
		if (IsNetworked && IsMultiplayerAuthority())
		{
			Rpc(method, args);
		}
	}

	private void PlayJumpFeedback(bool usedExtraJump)
	{
		RemoteJumpFeedback(usedExtraJump);
		BroadcastEffect(MethodName.RemoteJumpFeedback, usedExtraJump);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteJumpFeedback(bool usedExtraJump)
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
		RemoteLandFeedback(impactSpeed);
		BroadcastEffect(MethodName.RemoteLandFeedback, impactSpeed);
		// Signal stays authority-side only — its listeners (camera shake
		// etc.) belong to the owning player's presentation, not puppets.
		EmitSignal(SignalName.Landed, impactSpeed);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteLandFeedback(float impactSpeed)
	{
		if (_landParticles != null)
		{
			_landParticles.Emitting = false;
			_landParticles.Restart();
			_landParticles.Emitting = true;
		}

		float pitch = Mathf.Clamp(0.85f + impactSpeed / 700f, 0.85f, 1.25f);
		PlaySound(_landPlayer, pitch, pitch);
	}

private AudioStreamPlayer2D GetOrCreateAudioPlayer(string nodeName)
{
	AudioStreamPlayer2D player = GetNodeOrNull<AudioStreamPlayer2D>(nodeName);
	if (player != null) return player;

	player = new AudioStreamPlayer2D { Name = nodeName, Bus = "SFX" };
	AddChild(player);
	return player;
}

// Called by VoiceChatManager.BroadcastVoice when a chunk of decoded PCM16
// mono voice audio arrives for this peer. Only ever makes sense on a
// puppet — the sender-side self-echo filter already prevents this from
// being called on your own authority copy, but double-checking here
// matches this codebase's habit of not trusting a single choke point.
public void ReceiveVoiceChunk(byte[] pcm)
{
	if (IsMultiplayerAuthority()) return;

	if (_voicePlayback == null)
	{
		_voiceStream = new AudioStreamGenerator
		{
			MixRate = VoiceChatManager.WireSampleRateHz,
			BufferLength = 0.5f,
		};
		_voiceChatPlayer.Stream = _voiceStream;
		_voiceChatPlayer.Play();
		_voicePlayback = (AudioStreamGeneratorPlayback)_voiceChatPlayer.GetStreamPlayback();
	}

	int sampleCount = pcm.Length / 2;
	var frames = new Vector2[sampleCount];
	for (int i = 0; i < sampleCount; i++)
	{
		short raw = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
		float sample = raw / 32768f;
		frames[i] = new Vector2(sample, sample);
	}

	// No jitter buffer by design — if the network delivered faster than
	// playback is draining, drop this chunk rather than partially pushing
	// or blocking; prioritizes freshness over completeness.
	if (frames.Length <= _voicePlayback.GetFramesAvailable())
	{
		_voicePlayback.PushBuffer(frames);
	}

	_voiceIdleSecondsLeft = VoiceIdleTimeoutSeconds;
}

// Puppet-only, ticked from _PhysicsProcess's remote branch. Stops playback
// after a stretch of silence (PushToTalk released, or the sender
// disconnected) instead of leaving the generator idling forever — the next
// chunk after a gap just rebuilds it fresh in ReceiveVoiceChunk.
private void TickVoicePlayback(float delta)
{
	if (_voicePlayback == null) return;

	_voiceIdleSecondsLeft -= delta;
	if (_voiceIdleSecondsLeft <= 0f)
	{
		_voiceChatPlayer.Stop();
		_voicePlayback = null;
	}
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
		RemoteHurtFeedback();
		BroadcastEffect(MethodName.RemoteHurtFeedback);

		SceneTreeTimer recoveryTimer = GetTree().CreateTimer(0.25f);
		recoveryTimer.Timeout += () =>
		{
			if (IsInstanceValid(this) && _currentState != State.Dead) _currentState = State.Idle;
		};
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteHurtFeedback()
	{
		FlashHurt();
		_hurtPlayer.Stream = GetRandomHurtSound();
		PlaySound(_hurtPlayer, 0.95f, 1.05f);
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

	// The instant "whoosh" a dash starts with — an overbright cyan-white pop
	// on Sam's own sprite (values above 1 read as a hot flash rather than a
	// tint, same trick the additive dash-ghost trail below uses) that snaps
	// back to normal almost immediately. Pairs with the trailing afterimages
	// so the dash reads as a sudden burst of speed, not just a fast slide.
	private void FlashDashStart()
	{
		RemoteDashFlash();
		BroadcastEffect(MethodName.RemoteDashFlash);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteDashFlash()
	{
		if (_dashFlashTween != null && _dashFlashTween.IsValid())
		{
			_dashFlashTween.Kill();
		}

		_animatedSprite.Modulate = new Color(1.6f, 1.75f, 1.9f, 1f);
		_dashFlashTween = CreateTween();
		_dashFlashTween.TweenProperty(_animatedSprite, "modulate", Colors.White, 0.16f)
			.SetTrans(Tween.TransitionType.Expo)
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
			case "CrawlTurnaround":
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
	//
	// HealthComponent isn't networked — every peer's own machine independently
	// simulates collision against every player's replicated position
	// (including remote puppets), so a puppet copy can reach 0 HP and fire
	// Died purely locally on a machine that doesn't actually own that player.
	// CheckpointManager.RespawnPlayer() always repositions "whichever Sam is
	// this machine's own local authority" — with no guard here, a remote
	// puppet's spurious local death silently teleported the WRONG player (the
	// one actually watching), which is why the host got yanked back whenever
	// a peer died. Only the machine that truly owns this Sam may act on it.
	if (IsNetworked && !IsMultiplayerAuthority()) return;

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

// Hot-to-cool energy gradient the ghosts fade through, freshest to oldest —
// the same "thruster" language as the dash/double-jump particles' own
// Gradient_thruster_fade, so the trail reads as part of the same visual
// vocabulary rather than a generic grey afterimage. Values above 1 combined
// with the additive material below are what actually make it read as a hot
// glow instead of a tinted copy.
private static readonly Color DashGhostHotColor = new Color(1.5f, 1.7f, 1.9f, 1f);
private static readonly Color DashGhostCoolColor = new Color(0.3f, 0.55f, 0.85f, 1f);

private Sprite2D GetDashGhost(int index)
{
	while (_dashGhostPool.Count <= index)
	{
		_dashGhostAdditiveMaterial ??= new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
		var ghost = new Sprite2D { ZIndex = 10, Visible = false, Material = _dashGhostAdditiveMaterial };
		GetTree().CurrentScene.AddChild(ghost);
		_dashGhostPool.Add(ghost);
		_dashGhostTweens.Add(null);
	}
	return _dashGhostPool[index];
}

private void FireDashGhost(int poolIndex, Texture2D texture, Vector2 globalPosition, bool flipH, Color tint, float alpha, float scale, float fadeDuration)
{
	if (texture == null) return;

	Sprite2D ghost = GetDashGhost(poolIndex);
	Tween existingTween = _dashGhostTweens[poolIndex];
	if (existingTween != null && existingTween.IsValid())
		existingTween.Kill();

	ghost.Texture = texture;
	ghost.GlobalPosition = globalPosition;
	ghost.FlipH = flipH;
	// Slight stretch along the travel axis reads as a motion smear rather
	// than a stack of static duplicate poses.
	ghost.Scale = new Vector2(scale * 1.12f, scale * 0.94f);
	ghost.Modulate = new Color(tint.R, tint.G, tint.B, alpha);
	ghost.Visible = true;

	Tween tween = GetTree().CreateTween();
	_dashGhostTweens[poolIndex] = tween;
	// Expo-out front-loads the fade — a quick bright flash that falls away
	// fast rather than a slow, even linear dissolve — reading as a snap of
	// speed instead of a lingering ghost.
	tween.TweenProperty(ghost, "modulate:a", 0, fadeDuration)
		.SetTrans(Tween.TransitionType.Expo)
		.SetEase(Tween.EaseType.Out);
	tween.TweenCallback(Callable.From(() =>
	{
		if (IsInstanceValid(ghost)) ghost.Visible = false;
	}));
}

public void SpawnDashEffect()
{
	EmitDashExhaust(FacingDirection);
	BroadcastEffect(MethodName.RemoteDashEffect, FacingDirection);

	Texture2D fallbackTexture = null;
	if (_animatedSprite != null && _animatedSprite.SpriteFrames != null)
		fallbackTexture = _animatedSprite.SpriteFrames.GetFrameTexture(_animatedSprite.Animation, _animatedSprite.Frame);

	// Spawn up to 5 clones from the dash trail, evenly distributed from the
	// player's current position back to where the dash started.
	int framesToSpawn = Mathf.Min(DashGhostPoolSize, _dashTrailFrames.Count);
	if (framesToSpawn <= 0)
	{
		// No trail frames yet (e.g., dash started immediately) — a single fallback ghost at the player's position.
		FireDashGhost(0, fallbackTexture, GlobalPosition, _animatedSprite?.FlipH ?? false, DashGhostHotColor, 0.95f, 0.96f, 0.22f);
		return;
	}

	for (int i = 0; i < framesToSpawn; i++)
	{
		// i=0 is the newest trail frame (right where the player ended up) and
		// increasing i steps back toward the oldest frame (the point where
		// the dash actually started) — so the alpha/tint/scale falloff below,
		// which all fade out as i increases, read as "bright and full-size
		// next to the player, fading away toward the dash's start point"
		// instead of the reverse.
		int frameIndex = _dashTrailFrames.Count - 1 - (i * _dashTrailFrames.Count) / framesToSpawn;
		if (frameIndex < 0) frameIndex = 0;
		DashTrailFrame trailFrame = _dashTrailFrames[frameIndex];

		Texture2D texture = trailFrame.Texture != null ? trailFrame.Texture : fallbackTexture;
		float t = framesToSpawn > 1 ? (float)i / (framesToSpawn - 1) : 0f;
		Color tint = DashGhostHotColor.Lerp(DashGhostCoolColor, t);
		float alpha = 0.95f - (i * 0.15f);
		float scale = 1f - i * 0.04f;
		// Quick throughout — even the oldest ghost is gone well under half a
		// second, so the trail reads as a flash rather than lingering smoke.
		float fadeDuration = 0.16f + (i * 0.045f);

		FireDashGhost(i, texture, trailFrame.Position, trailFrame.FlipH, tint, alpha, scale, fadeDuration);
	}
}

private void EmitDashExhaust(float facing)
{
	if (_dashParticles == null) return;

	// Exhaust kicks backward out of the dash, whichever way it went. The
	// facing arrives as an argument because remote copies never update
	// their own FacingDirection.
	_dashParticles.Direction = new Vector2(-facing, 0f);
	_dashParticles.Emitting = true;
	SceneTreeTimer particleTimer = GetTree().CreateTimer(0.35f);
	particleTimer.Timeout += () =>
	{
		if (IsInstanceValid(this) && _dashParticles != null) _dashParticles.Emitting = false;
	};
}

// Puppet copies have no dash-trail history, so they get the exhaust plus a
// single afterimage of the current (frame-synced) pose — enough to sell the
// blink without shipping the whole trail across the network.
[Rpc(MultiplayerApi.RpcMode.Authority)]
private void RemoteDashEffect(float facing)
{
	EmitDashExhaust(facing);

	Texture2D texture = null;
	if (_animatedSprite != null && _animatedSprite.SpriteFrames != null)
		texture = _animatedSprite.SpriteFrames.GetFrameTexture(_animatedSprite.Animation, _animatedSprite.Frame);
	FireDashGhost(0, texture, GlobalPosition, _animatedSprite?.FlipH ?? false, DashGhostHotColor, 0.95f, 0.96f, 0.22f);
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
