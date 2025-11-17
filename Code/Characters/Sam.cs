using Godot;
using System;

using CpuParticles2D = Godot.CpuParticles2D;
using AnimatedSprite2D = Godot.AnimatedSprite2D;
using PointLight2D = Godot.PointLight2D;

public partial class Sam : CharacterBody2D
{
	[Export] public float Speed = 150f; // Normal walking speed
	[Export] public float SprintSpeed = 250f; // Running speed when sprinting
	[Export] public float Acceleration = 800f; // How fast player accelerates
	[Export] public float Deceleration = 1000f; // How fast player decelerates
	[Export] public float Friction = 0.2f; // Unused friction value
	[Export] public float JumpVelocity = -300f; // Initial jump force
	[Export] public float Gravity = 750f; // Gravity applied every frame
	[Export] public float MaxFallSpeed = 350f; // Maximum falling speed
	[Export] public bool CanDoubleJump = true; // Whether player can double jump
	[Export] public float CoyoteTime = 0.15f; // Grace period for jumping after leaving ground
	[Export] public float JumpBufferTime = 0.15f; // Input buffer for jump presses
	[Export] public int MaxHealth = 200; // Maximum health points

	private AnimatedSprite2D _animatedSprite; // Player sprite with animations
	private CpuParticles2D _jumpParticles;
	private CpuParticles2D _doubleJumpParticles;
	private CpuParticles2D _slideParticles;
	private CpuParticles2D _landParticles;
	private enum State { Idle, Walk, Run, Jump, Fall, Float, Land, AirRoll, Sliding, CrouchIdle, CrouchWalk, Roll, Hurt, Death, WallLand, WallSlide, Push, Pull } // Player animation states
	private State _currentState = State.Idle; // Current animation state
	private bool _facingRight = true; // Sprite facing direction
	private int _jumpCount = 0; // Number of jumps performed
	private int _maxJumps = 1; // Maximum jumps allowed
	private float _coyoteTimer = 0f; // Coyote time remaining
	private float _jumpBufferTimer = 0f; // Jump buffer time remaining
	private bool _wasOnFloor = false; // Was player on floor last frame
	private Vector2 _velocityBeforeStop = Vector2.Zero; // Velocity before stopping
	private bool _isSliding = false; // Whether player is sliding
	private float _slideTimer = 0f; // Slide duration remaining
	private float _slideDuration = 0.8f; // Total slide animation length
	private bool _landAnimationFinished = false; // Whether landing animation completed
	private bool _isCrouching = false; // Whether player is crouching
	private bool _flashlightOn = false; // Flashlight state
	private bool _isSprinting = false; // Whether player is sprinting
	private bool _isWallSliding = false; // Whether player is wall sliding
	private RigidBody2D _currentPushable = null; // Currently interacting pushable object
	private bool _isInteractingWithPushable = false; // Whether we're pushing or pulling
	private RigidBody2D _objectInRange = null; // Tracked object in grab area
	private Area2D _grabArea; // Grab area for detecting pushable objects
	private bool _hasWallJumped = false; // Whether player has already wall jumped this air time
	private float _wallJumpInputTimer = 0f; // Timer for temporary input inversion after wall jump
	private float _wallJumpInputDuration = 0.3f; // How long input stays inverted
	private int _currentHealth; // Current health points
	private bool _isInvulnerable = false; // Invulnerability state
	private float _hurtTimer = 0f; // Invulnerability time remaining
	private float _invulnerabilityDuration = 0.5f; // Total invulnerability duration
	private float _damageCooldownTimer = 0f; // Time until next damage allowed
	private float _damageCooldownDuration = 0.3f; // Minimum time between damage instances
	private bool _isDead = false; // Death state
	private float _savedObjectLinearDamp = -1f;
	private float _savedObjectAngularDamp = -1f;
	private bool _wasAdjustingObjectDamping = false;
	private CollisionShape2D _normalCollision;
	private CollisionShape2D _crouchCollision;
	private PointLight2D _characterLight;
	private Light2D _coneLight;
	private AudioStreamPlayer2D _footstepPlayer;
	private AudioStreamPlayer2D _jumpPlayer;
	private AudioStreamPlayer2D _hurtPlayer;
	private float _footstepTimer = 0f;
	private float _footstepInterval = 0.4f; // Base interval for footsteps (faster)
	private float _painCooldownTimer = 0f;
	private float _painCooldownDuration = 1.0f; // 1 second cooldown
	private RandomNumberGenerator _rng = new RandomNumberGenerator();
	private bool _deathSoundPlayed = false; // Tracks if death sound has been played

	public override void _Ready()
	{
		// Initialize node references
		_animatedSprite = GetNode<AnimatedSprite2D>("AnimatedSprite2D");
		_jumpParticles = GetNode<CpuParticles2D>("JumpParticles");
		_doubleJumpParticles = GetNode<CpuParticles2D>("DoubleJumpParticles");
		_slideParticles = GetNode<CpuParticles2D>("SlideParticles");
		_landParticles = GetNode<CpuParticles2D>("LandParticles");
		_normalCollision = GetNode<CollisionShape2D>("CollisionShape2D");
		_crouchCollision = GetNode<CollisionShape2D>("Crouch_Roll");
		_characterLight = GetNode<PointLight2D>("PointLight2D");
		_coneLight = GetNode<Light2D>("ConeLight");
		_footstepPlayer = GetNode<AudioStreamPlayer2D>("FootstepPlayer");
		_jumpPlayer = GetNode<AudioStreamPlayer2D>("JumpPlayer");
		_hurtPlayer = GetNode<AudioStreamPlayer2D>("HurtPlayer");
		_grabArea = GetNode<Area2D>("GrabArea");
		_grabArea.BodyEntered += OnGrabEntered;
		_grabArea.BodyExited += OnGrabExited;

		// Setup initial state
		_coneLight.Enabled = false;
		_characterLight.Enabled = false;
		_maxJumps = CanDoubleJump ? 2 : 1;
		_currentHealth = MaxHealth;

		// Connect animation finished signal with proper Godot signature
		_animatedSprite.AnimationFinished += () => OnAnimationFinished(_animatedSprite.Animation);
	}

	public override void _Process(double delta)
	{
		float deltaTime = (float)delta;

		// Skip input processing if dead, but allow death animation to play
		if (_isDead)
		{
			// Only handle death animation here
			PlayAnimation();
			return;
		}

		// Handle input for state changes
		bool crawlPressed = Input.IsActionPressed("Crawl");
		bool sprintPressed = Input.IsActionPressed("Sprint");
		bool crawlJustPressed = Input.IsActionJustPressed("Crawl");
		bool flashlightJustPressed = Input.IsActionJustPressed("Flashlight");

		// Toggle flashlight
		if (flashlightJustPressed)
		{
			_flashlightOn = !_flashlightOn;
			_characterLight.Enabled = _flashlightOn;
			_coneLight.Enabled = _flashlightOn;
		}

		// Combat roll: sprinting + sudden crouch press (always dashes forward in facing direction)
		if (crawlJustPressed && sprintPressed && IsOnFloorOnly() &&
			(_currentState == State.Run || _currentState == State.Sliding))
		{
			_currentState = State.Roll;
			float dashDirection = _facingRight ? 1f : -1f;
			float dashSpeed = SprintSpeed * 2f; // Strong forward dash
			Velocity = new Vector2(dashDirection * dashSpeed, Velocity.Y);
		}
		else if (crawlPressed && IsOnFloorOnly() && _currentState != State.Roll)
		{
			if (!_isCrouching)
			{
				_isCrouching = true;
				_normalCollision.Disabled = true;
				_crouchCollision.Disabled = false;
			}
		}
		else if (!crawlPressed && _isCrouching)
		{
			_isCrouching = false;
			_normalCollision.Disabled = false;
			_crouchCollision.Disabled = true;
		}

		// Update flashlight cone rotation to follow mouse
		if (_flashlightOn)
		{
			Vector2 mousePos = GetViewport().GetMousePosition();
			Vector2 playerPos = GetGlobalTransformWithCanvas().Origin;
			Vector2 direction = mousePos - playerPos;
			float angle = Mathf.Atan2(direction.Y, direction.X);
			_coneLight.Position = Vector2.Zero;
			_coneLight.Rotation = angle - Mathf.Pi / 2;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		float deltaTime = (float)delta;

		// Skip all processing if dead
		if (_isDead) return;

		Vector2 inputDir = Input.GetVector("Left", "Right", "Jump", "Crawl");
		bool jumpPressed = Input.IsActionJustPressed("Jump");
		bool jumpHeld = Input.IsActionPressed("Jump");
		bool sprintPressed = Input.IsActionPressed("Sprint");

		// Handle wall jump input inversion timer
		if (_wallJumpInputTimer > 0)
		{
			_wallJumpInputTimer -= deltaTime;
		}

		// Wall sliding detection - requires input towards the wall
		bool isNearWall = IsOnWallOnly() && !IsOnFloor();
		bool inputTowardsWall = false;

		if (isNearWall)
		{
			// Determine which direction the wall is
			if (Velocity.X > 0 && inputDir.X > 0) // Wall to the right, input right
			{
				inputTowardsWall = true;
			}
			else if (Velocity.X < 0 && inputDir.X < 0) // Wall to the left, input left
			{
				inputTowardsWall = true;
			}
		}

		if (isNearWall && inputTowardsWall && !_isWallSliding)
		{
			StartWallSlide();
		}
		else if ((!isNearWall || !inputTowardsWall) && _isWallSliding)
		{
			StopWallSlide();
		}

		// Handle hurt invulnerability timer
		if (_isInvulnerable)
		{
			_hurtTimer -= deltaTime;
			if (_hurtTimer <= 0)
			{
				_isInvulnerable = false;
			}
		}

		// Handle damage cooldown timer
		if (_damageCooldownTimer > 0)
		{
			_damageCooldownTimer -= deltaTime;
		}

		// Handle pain sound cooldown timer
		if (_painCooldownTimer > 0)
		{
			_painCooldownTimer -= deltaTime;
		}

		// Interrupt landing animation if player attempts to move
		if (_currentState == State.Land && inputDir.X != 0)
		{
			_currentState = _isSprinting ? State.Run : State.Walk;
		}

		if (IsOnFloorOnly())
		{
			_coyoteTimer = CoyoteTime;
			_jumpCount = 0;
			_hasWallJumped = false; // Reset wall jump when on ground
			_wasOnFloor = true;
		}
		else
		{
			_coyoteTimer -= deltaTime;
		}

		if (jumpPressed)
		{
			_jumpBufferTimer = JumpBufferTime;
		}
		else
		{
			_jumpBufferTimer -= deltaTime;
		}

		float currentSpeed;
		if (_isCrouching)
		{
			sprintPressed = false; // Prevent sprinting while crouching
			currentSpeed = Speed * 0.5f;
		}
		else
		{
			currentSpeed = sprintPressed ? SprintSpeed : Speed;
		}
		_isSprinting = sprintPressed;

		// Handle input inversion after wall jump
		float effectiveInputDirX = inputDir.X;
		if (_wallJumpInputTimer > 0)
		{
			effectiveInputDirX *= -1f; // Invert horizontal input temporarily
		}

		float targetSpeed = effectiveInputDirX * currentSpeed;

		if (_isSliding)
		{
			_slideTimer -= deltaTime;
			if (_slideTimer <= 0 || Mathf.Abs(Velocity.X) < 50f)
			{
				_isSliding = false;
				_slideParticles.Emitting = false; // Stop slide particles
				if (_currentState == State.Sliding)
				{
					_currentState = State.Float;
				}
			}
			else
			{
				float slideFriction = 0.95f;
				Velocity = new Vector2(Velocity.X * slideFriction, Velocity.Y);
			}
		}
		else if (targetSpeed != 0)
		{
			Velocity = Velocity.MoveToward(new Vector2(targetSpeed, Velocity.Y), Acceleration * deltaTime);
		}
		else
		{
			if (Mathf.Abs(Velocity.X) > SprintSpeed * 0.8f && IsOnFloorOnly() && !_isCrouching)
			{
				_isSliding = true;
				_slideTimer = _slideDuration;
				_currentState = State.Sliding;
				_slideParticles.Emitting = true;
			}
			else
			{
				Velocity = Velocity.MoveToward(new Vector2(0, Velocity.Y), Deceleration * deltaTime);
			}
		}

		if (!IsOnFloorOnly())
		{
			float currentGravity = Gravity;
			if (_isWallSliding)
			{
				currentGravity *= 0.15f; // Reduce gravity more during wall slide for slower descent
			}
			Velocity += new Vector2(0, currentGravity * deltaTime);
			if (Velocity.Y > MaxFallSpeed)
			{
				Velocity = new Vector2(Velocity.X, MaxFallSpeed);
			}
		}
		else
		{
			if (Velocity.Y > 0)
			{
				Velocity = new Vector2(Velocity.X, 0);
			}
		}

		bool canJump = (_coyoteTimer > 0 || _jumpCount < _maxJumps) && _jumpBufferTimer > 0;
		if (canJump)
		{
			if (_jumpCount == 0 || CanDoubleJump)
			{
				Velocity = new Vector2(Velocity.X, JumpVelocity);
				_jumpCount++;
				_coyoteTimer = 0f;
				_jumpBufferTimer = 0f;
				_currentState = _jumpCount == 1 ? State.Jump : State.AirRoll;

				// Play jump sound
				_jumpPlayer.Stream = GD.Load<AudioStream>("res://Sound/Footsteps/30_Jump_03.wav");
				_jumpPlayer.Play();

				// Play particles based on jump type
				if (_jumpCount == 1)
				{
					_jumpParticles.Restart(); // Regular jump particles
				}
				else
				{
					_doubleJumpParticles.Restart(); // Double jump particles
				}
			}
		}

		// Wall jumping
		if (jumpPressed && _isWallSliding && !_hasWallJumped && _jumpBufferTimer > 0)
		{
			// Determine wall direction and jump away from it
			float wallDirection = Velocity.X > 0 ? -1f : 1f; // If velocity positive (wall right), jump left; if negative (wall left), jump right
			float wallJumpForce = JumpVelocity * 1.4f; // Higher jump for better distance
			float wallJumpHorizontal = Speed * 2.2f; // Much stronger horizontal push away from wall

			Velocity = new Vector2(wallDirection * wallJumpHorizontal, wallJumpForce);
			_hasWallJumped = true;
			_isWallSliding = false; // Stop wall sliding
			_wallJumpInputTimer = _wallJumpInputDuration; // Start input inversion timer
			_jumpBufferTimer = 0f;
			_currentState = State.Jump;

			// Play jump sound
			_jumpPlayer.Stream = GD.Load<AudioStream>("res://Sound/Footsteps/30_Jump_03.wav");
			_jumpPlayer.Play();

			_jumpParticles.Restart(); // Regular jump particles
		}

		if (!jumpHeld && Velocity.Y < 0 && (_currentState == State.Jump || _currentState == State.AirRoll || _currentState == State.Float))
		{
			Velocity = new Vector2(Velocity.X, Velocity.Y * 0.5f);
		}

		bool suddenStop = _wasOnFloor && !IsOnFloor() && Mathf.Abs(Velocity.X) > Speed * 0.5f;
		if (suddenStop)
		{
			_velocityBeforeStop = Velocity;
		}
		else if (IsOnFloor())
		{
			_velocityBeforeStop = Vector2.Zero;
		}

		MoveAndSlide();

		// Handle pushable object interaction
		HandlePushableInteraction(deltaTime);

		if (Velocity.X > 0 && !_isWallSliding)
		{
			_facingRight = true;
			_animatedSprite.FlipH = false;
		}
		else if (Velocity.X < 0 && !_isWallSliding)
		{
			_facingRight = false;
			_animatedSprite.FlipH = true;
		}

		// Death state is handled in _Process now

		// Footstep sound system
		HandleFootsteps(deltaTime);

		// Only update state and play animation if not dead
		if (!_isDead)
		{
			UpdateState(deltaTime);
			PlayAnimation();
		}

		// Apply hurt knockback
		if (_currentState == State.Hurt)
		{
			Velocity = Velocity.MoveToward(Vector2.Zero, 1000 * deltaTime);
		}

		_wasOnFloor = IsOnFloorOnly();
	}

	private void OnAnimationFinished(string animationName)
	{
		if (animationName == "Land")
		{
			_landAnimationFinished = true;
		}
		else if (animationName == "AirRoll")
		{
			if (_currentState == State.AirRoll)
			{
				_currentState = Velocity.Y > 0 ? State.Fall : State.Jump;
			}
		}
		else if (animationName == "Roll")
		{
			if (_currentState == State.Roll)
			{
				if (Mathf.Abs(Velocity.X) > 10f)
				{
					_currentState = _isSprinting ? State.Run : State.Walk;
				}
				else
				{
					_currentState = State.Idle;
				}
			}
		}
		else if (animationName == "Hurt")
		{
			if (_currentState == State.Hurt)
			{
				if (Mathf.Abs(Velocity.X) > 10f)
				{
					_currentState = _isSprinting ? State.Run : State.Walk;
				}
				else
				{
					_currentState = State.Idle;
				}
			}
		}
		else if (animationName == "WallLand")
		{
			if (_currentState == State.WallLand)
			{
				_currentState = State.WallSlide;
			}
		}
		else if (animationName == "AirSlide")
		{
			// Air slide animation finished - this shouldn't happen since we removed air slide, but just in case
		}
		else if (animationName == "Death")
		{
			if (_currentState == State.Death)
			{
				// Clean up and exit
				SetProcess(false);
				SetPhysicsProcess(false);
				Visible = false; // Hide player after death animation
				GetTree().Quit();
			}
		}
	}

	private void UpdateState(float deltaTime)
	{
		State newState = _currentState;

		if (IsOnFloor())
		{
			_jumpCount = 0; // Reset jump count when on ground

			if (_currentState == State.Fall || _currentState == State.Jump || _currentState == State.Float || _currentState == State.AirRoll)
			{
				bool isSprinting = _isSprinting || Mathf.Abs(Velocity.X) > Speed * 1.2f;

				if (isSprinting)
				{
					newState = State.Run;
					_landAnimationFinished = true;
				}
				else
				{
					if (_currentState != State.Land)
					{
						newState = State.Land;
						_landAnimationFinished = false;
						_landParticles.Restart();
						// Play landing sound
						_jumpPlayer.Stream = GD.Load<AudioStream>("res://Sound/Footsteps/45_Landing_01.wav");
						_jumpPlayer.Play();
					}
				}
			}
			else if (_currentState == State.Sliding && !_isSliding)
			{
				newState = State.Float; // Sliding stops, go to float
			}
			else if (_currentState == State.Land && _landAnimationFinished)
			{
				// Land animation finished, transition to appropriate state
				if (Mathf.Abs(Velocity.X) > 10f)
				{
					newState = _isSprinting ? State.Run : State.Walk;
				}
				else
				{
					newState = State.Idle;
				}
			}
			else if (_currentState != State.Land && _currentState != State.Sliding && _currentState != State.Roll && !_isInteractingWithPushable)
			{
				if (_isCrouching)
				{
					if (Mathf.Abs(Velocity.X) > 10f)
					{
						newState = State.CrouchWalk;
					}
					else
					{
						newState = State.CrouchIdle;
					}
				}
				else if (Mathf.Abs(Velocity.X) > 10f && !_isSliding)
				{
					newState = _isSprinting ? State.Run : State.Walk;
				}
				else if (!_isSliding)
				{
					newState = State.Idle;
				}
			}
		}
		else
		{
			// Air states - strict priority
			if (_currentState == State.Land)
			{
				// Land animation must complete, can't be interrupted by air states
				// The animation finished signal will handle transitioning out of Land
				return;
			}
			else if (_currentState == State.WallLand)
			{
				// WallLand animation must complete first
				return;
			}
			else if (_isWallSliding && _currentState != State.WallSlide)
			{
				newState = State.WallLand;
			}
			else if (_currentState == State.WallSlide && !_isWallSliding)
			{
				newState = State.Fall;
			}
			else if (_currentState == State.WallSlide && !IsOnWallOnly())
			{
				// Force exit wall slide state if not actually on wall
				newState = State.Fall;
			}
			else if (_currentState == State.AirRoll)
			{
				// AirRoll animation must complete fully, only interrupted by landing on floor
				// Don't transition to other states while AirRoll is playing
				if (_animatedSprite.Animation == "AirRoll" && !_animatedSprite.IsPlaying())
				{
					newState = Velocity.Y > 0 ? State.Fall : State.Jump;
				}
			}
			else if (_currentState == State.Jump && Velocity.Y > 0)
			{
				newState = State.Fall;
			}
			else if (_currentState == State.Sliding)
			{
				// Sliding continues until it naturally stops
			}
			else if (_currentState == State.Roll)
			{
				// Roll continues until animation finishes
			}
			else if (_velocityBeforeStop != Vector2.Zero && Mathf.Abs(Velocity.X) < 50f && Velocity.Y > 0 && _currentState != State.AirRoll)
			{
				// Only float as fallback when sliding stops and we're falling slowly
				newState = State.Float;
				_velocityBeforeStop = Vector2.Zero;
			}
			else if (Velocity.Y > 0 && _currentState != State.Fall && _currentState != State.Float && _currentState != State.AirRoll)
			{
				newState = State.Fall;
			}
		}

		// Prevent state changes while hurt or dead (unless animations finish)
		if ((_currentState == State.Hurt && newState != State.Hurt) ||
			(_currentState == State.Death && newState != State.Death))
		{
			return;
		}

		if (_currentState != newState)
		{
			_currentState = newState;
		}
	}

	private void UpdateState_Old(float deltaTime)
	{
		State newState = _currentState;

		if (IsOnFloor())
		{
			_jumpCount = 0; // Reset jump count when on ground

			if (_currentState == State.Fall || _currentState == State.Jump || _currentState == State.Float || _currentState == State.AirRoll)
			{
				bool isSprinting = _isSprinting || Mathf.Abs(Velocity.X) > Speed * 1.2f;

				if (isSprinting)
				{
					newState = State.Run;
					_landAnimationFinished = true;
				}
				else
				{
					if (_currentState != State.Land)
					{
						newState = State.Land;
						_landAnimationFinished = false;
						_landParticles.Restart();
						// Play landing sound
						_jumpPlayer.Stream = GD.Load<AudioStream>("res://Sound/Footsteps/45_Landing_01.wav");
						_jumpPlayer.Play();
					}
				}
			}
			else if (_currentState == State.Sliding && !_isSliding)
			{
				newState = State.Float; // Sliding stops, go to float
			}
			else if (_currentState == State.Land && _landAnimationFinished)
			{
				// Land animation finished, transition to appropriate state
				if (Mathf.Abs(Velocity.X) > 10f)
				{
					newState = _isSprinting ? State.Run : State.Walk;
				}
				else
				{
					newState = State.Idle;
				}
			}
			else if (_currentState != State.Land && _currentState != State.Sliding && _currentState != State.Roll)
			{
				if (_isCrouching)
				{
					if (Mathf.Abs(Velocity.X) > 10f)
					{
						newState = State.CrouchWalk;
					}
					else
					{
						newState = State.CrouchIdle;
					}
				}
				else if (Mathf.Abs(Velocity.X) > 10f && !_isSliding)
				{
					newState = _isSprinting ? State.Run : State.Walk;
				}
				else if (!_isSliding)
				{
					newState = State.Idle;
				}
			}
		}
		else
		{
			// Air states - strict priority
			if (_currentState == State.Land)
			{
				// Land animation must complete, can't be interrupted by air states
				// The animation finished signal will handle transitioning out of Land
				return;
			}
			else if (_currentState == State.WallLand)
			{
				// WallLand animation must complete first
				return;
			}
			else if (_isWallSliding && _currentState != State.WallSlide)
			{
				newState = State.WallLand;
			}
			else if (_currentState == State.WallSlide && !_isWallSliding)
			{
				newState = State.Fall;
			}
			else if (_currentState == State.WallSlide && !IsOnWallOnly())
			{
				// Force exit wall slide state if not actually on wall
				newState = State.Fall;
			}
			else if (_currentState == State.AirRoll)
			{
				// AirRoll animation must complete fully, only interrupted by landing on floor
				// Don't transition to other states while AirRoll is playing
				if (_animatedSprite.Animation == "AirRoll" && !_animatedSprite.IsPlaying())
				{
					newState = Velocity.Y > 0 ? State.Fall : State.Jump;
				}
			}
			else if (_currentState == State.Jump && Velocity.Y > 0)
			{
				newState = State.Fall;
			}
			else if (_currentState == State.Sliding)
			{
				// Sliding continues until it naturally stops
			}
			else if (_currentState == State.Roll)
			{
				// Roll continues until animation finishes
			}
			else if (_velocityBeforeStop != Vector2.Zero && Mathf.Abs(Velocity.X) < 50f && Velocity.Y > 0 && _currentState != State.AirRoll)
			{
				// Only float as fallback when sliding stops and we're falling slowly
				newState = State.Float;
				_velocityBeforeStop = Vector2.Zero;
			}
			else if (Velocity.Y > 0 && _currentState != State.Fall && _currentState != State.Float && _currentState != State.AirRoll)
			{
				newState = State.Fall;
			}
		}

		// Prevent state changes while hurt or dead (unless animations finish)
		if ((_currentState == State.Hurt && newState != State.Hurt) ||
			(_currentState == State.Death && newState != State.Death))
		{
			return;
		}

		if (_currentState != newState)
		{
			_currentState = newState;
		}
	}

	private void HandleFootsteps(float deltaTime)
	{
		// Only play footsteps when on ground and moving, and not in sliding/rolling states
		if (!IsOnFloor() || _isDead || _isSliding || _currentState == State.Roll) return;

		// Determine footstep interval based on speed (faster overall)
		float currentInterval = _footstepInterval;
		if (_isSprinting)
		{
			currentInterval *= 0.8f; // Faster footsteps when sprinting
		}
		else if (_isCrouching)
		{
			currentInterval *= 1.4f; // Slightly slower footsteps when crouching
		}
		else
		{
			currentInterval *= 1.0f; // Base speed for normal walking
		}

		// Only play footsteps if moving horizontally
		if (Mathf.Abs(Velocity.X) > 10f)
		{
			_footstepTimer += deltaTime;
			if (_footstepTimer >= currentInterval)
			{
				_footstepTimer = 0f;
				PlayFootstepSound(_isSprinting);
			}
		}
	}

	private void PlayFootstepSound(bool isSprinting = false)
	{
		// Use only floor sounds for all movement types
		int soundIndex = _rng.RandiRange(1, 5);
		string soundPath = $"res://Sound/Footsteps/floor{soundIndex}.ogg";

		_footstepPlayer.Stream = GD.Load<AudioStream>(soundPath);

		// Adjust pitch and speed based on movement state
		if (_isCrouching)
		{
			_footstepPlayer.PitchScale = 0.7f; // Lower pitch for crouching
		}
		else if (isSprinting)
		{
			_footstepPlayer.PitchScale = 1.4f; // Higher pitch for sprinting
		}
		else
		{
			_footstepPlayer.PitchScale = 1.0f; // Normal pitch for walking
		}

		_footstepPlayer.Play();
	}

	private void PlayAnimation()
	{
		string animationName = _currentState switch
		{
			State.Idle => "Idle",
			State.Walk => "Walk",
			State.Run => "Run",
			State.Jump => "Jump",
			State.Fall => "Fall",
			State.Float => "Float",
			State.Land => "Land",
			State.AirRoll => "AirRoll",
			State.Sliding => "Slide",
			State.CrouchIdle => "CrouchIdle",
			State.CrouchWalk => "CrouchWalk",
			State.Roll => "Roll",
			State.Hurt => "Hurt",
			State.Death => "Death",
			State.WallLand => "Wallland",
			State.WallSlide => "Wallslide",
			State.Push => "Push",
			State.Pull => "Pull",
			_ => "Idle"
		};

		// Only change animation if it's actually different or if we're in a death state (force restart)
		if (_animatedSprite.Animation != animationName || _currentState == State.Death)
		{
			_animatedSprite.Play(animationName);
			GD.Print($"Playing animation: {animationName} for state: {_currentState}");

			// Play death sound when death animation starts
			if (animationName == "Death" && !_deathSoundPlayed)
			{
				_hurtPlayer.Stream = GD.Load<AudioStream>("res://Sound/voice/human_female_preburst1.ogg");
				_hurtPlayer.Play();
				_deathSoundPlayed = true;
			}
		}
	}

	private void PlayAnimation_Old()
	{
		string animationName = _currentState switch
		{
			State.Idle => "Idle",
			State.Walk => "Walk",
			State.Run => "Run",
			State.Jump => "Jump",
			State.Fall => "Fall",
			State.Float => "Float",
			State.Land => "Land",
			State.AirRoll => "AirRoll",
			State.Sliding => "Slide",
			State.CrouchIdle => "CrouchIdle",
			State.CrouchWalk => "CrouchWalk",
			State.Roll => "Roll",
			State.Hurt => "Hurt",
			State.Death => "Death",
			State.WallLand => "Wallland",
			State.WallSlide => "Wallslide",
			_ => "Idle"
		};

		// Only change animation if it's actually different or if we're in a death state (force restart)
		if (_animatedSprite.Animation != animationName || _currentState == State.Death)
		{
			_animatedSprite.Play(animationName);
			GD.Print($"Playing animation: {animationName} for state: {_currentState}");

			// Play death sound when death animation starts
			if (animationName == "Death" && !_deathSoundPlayed)
			{
				_hurtPlayer.Stream = GD.Load<AudioStream>("res://Sound/voice/human_female_preburst1.ogg");
				_hurtPlayer.Play();
				_deathSoundPlayed = true;
			}
		}
	}

	public void TakeDamage(int damage, Vector2 knockbackDirection)
	{
		if (_isInvulnerable || _isDead || _damageCooldownTimer > 0) return; // Check all damage prevention conditions

		_currentHealth -= damage;
		_currentHealth = Mathf.Max(0, _currentHealth);

		// Set damage cooldown to prevent spam damage
		_damageCooldownTimer = _damageCooldownDuration;

		GD.Print($"Player took {damage} damage. Health: {_currentHealth}");

		if (_currentHealth <= 0)
		{
			_isDead = true; // Mark player as dead
			_currentState = State.Death; // Immediately set death state
			Velocity = Vector2.Zero; // Stop all movement
			GD.Print("Player died - entering death state");
		}
		else
		{
			// Play hurt sounds with cooldown
			if (_painCooldownTimer <= 0)
			{
				// Randomly choose between the two pain sounds
				string painSound = _rng.RandiRange(0, 1) == 0 ?
					"res://Sound/voice/human_female_pain_4.ogg" :
					"res://Sound/voice/human_female_pain_5.ogg";
				_hurtPlayer.Stream = GD.Load<AudioStream>(painSound);
				_hurtPlayer.Play();

				_painCooldownTimer = _painCooldownDuration;
			}

			Velocity = knockbackDirection * 200f; // Apply knockback
			_currentState = State.Hurt; // Set hurt state
			_isInvulnerable = true; // Enable temporary invulnerability
			_hurtTimer = _invulnerabilityDuration;
		}
	}

	public int GetHealth()
	{
		return _currentHealth;
	}


	private void StartWallSlide()
	{
		_isWallSliding = true;
		// Set facing direction away from the wall
		if (Velocity.X > 0) // Wall to the right, face left
		{
			_facingRight = false;
			_animatedSprite.FlipH = true;
		}
		else if (Velocity.X < 0) // Wall to the left, face right
		{
			_facingRight = true;
			_animatedSprite.FlipH = false;
		}
		// Wall sliding logic is handled in physics process
	}

	private void StopWallSlide()
	{
		_isWallSliding = false;
		if (_currentState == State.WallSlide)
		{
			_currentState = State.Fall;
		}
	}

	private void OnGrabEntered(Node2D body)
	{
		if (body is RigidBody2D rb && rb.IsInGroup("pushable"))
		{
			_objectInRange = rb;
			GD.Print($"Object entered grab area: {body.Name}");
		}
	}

	private void OnGrabExited(Node2D body)
	{
		if (body is RigidBody2D rb && rb == _objectInRange)
		{
			_objectInRange = null;
			GD.Print($"Object exited grab area: {body.Name}");
		}
	}

	private void HandlePushableInteraction(float deltaTime)
	{
		bool interactPressed = Input.IsActionPressed("Interact");
		Vector2 inputDir = Input.GetVector("Left", "Right", "Jump", "Crawl");

		// STOP INTERACTION (restore damping + reset state)
		if (!interactPressed || _objectInRange == null)
		{
			if (_wasAdjustingObjectDamping && _objectInRange != null)
				RestoreObjectDamping(_objectInRange);

			_isInteractingWithPushable = false;
			_currentPushable = null;

			return;
		}

		// Must be horizontally aligned
		float yDifference = Mathf.Abs(_objectInRange.GlobalPosition.Y - GlobalPosition.Y);
		if (yDifference > 20f)
		{
			if (_wasAdjustingObjectDamping)
				RestoreObjectDamping(_objectInRange);

			_isInteractingWithPushable = false;
			_currentPushable = null;
			return;
		}

		// Start interacting
		_isInteractingWithPushable = true;
		_currentPushable = _objectInRange;

		// Wake object and tune damping for responsiveness
		WakeAndTuneObjectForInteraction(_objectInRange);

		// Required variables
		float playerMoveSpeed = 39f;       // slow movement while dragging
		float objectTargetSpeed = 55f;     // desired rigidbody speed
		float velGain = 200f;              // force multiplier
		float maxForce = 2000f;            // safety clamp

		float dir = Mathf.Sign(inputDir.X);
		float relativeX = _objectInRange.GlobalPosition.X - GlobalPosition.X;

		bool pushing =
			(relativeX > 0 && inputDir.X > 0) ||
			(relativeX < 0 && inputDir.X < 0);

		bool pulling =
			(relativeX > 0 && inputDir.X < 0) ||
			(relativeX < 0 && inputDir.X > 0);

		// No horizontal input = stop
		if (Mathf.Abs(inputDir.X) < 0.1f)
		{
			// stop player
			Velocity = new Vector2(0, Velocity.Y);

			// gently brake object
			Vector2 brake = -_objectInRange.LinearVelocity * (_objectInRange.Mass * 4f);
			_objectInRange.ApplyCentralForce(brake);

			_objectInRange.AngularVelocity = 0;
			_currentState = State.Idle;
			return;
		}

		// Set correct animation state
		if (pushing) _currentState = State.Push;
		else if (pulling) _currentState = State.Pull;
		else _currentState = State.Idle;

		// Player movement
		Velocity = new Vector2(dir * playerMoveSpeed, Velocity.Y);

		// OBJECT VELOCITY CONTROLLER
		float desiredVx = dir * objectTargetSpeed;
		float velError = desiredVx - _objectInRange.LinearVelocity.X;

		float force = velError * _objectInRange.Mass * velGain;
		force = Mathf.Clamp(force, -maxForce, maxForce);

		_objectInRange.ApplyCentralForce(new Vector2(force, 0));

		// No rotation during interaction
		_objectInRange.AngularVelocity = 0f;
		_objectInRange.ApplyTorque(0f);

		// Safety clamp
		float vx = _objectInRange.LinearVelocity.X;
		float limit = objectTargetSpeed + 10f;
		if (vx > limit)
			_objectInRange.LinearVelocity = new Vector2(limit, _objectInRange.LinearVelocity.Y);
		if (vx < -limit)
			_objectInRange.LinearVelocity = new Vector2(-limit, _objectInRange.LinearVelocity.Y);
	}

	private void WakeAndTuneObjectForInteraction(RigidBody2D obj)
	{
		if (obj == null) return;

		obj.Sleeping = false;

		if (!_wasAdjustingObjectDamping)
		{
			_savedObjectLinearDamp = obj.LinearDamp;
			_savedObjectAngularDamp = obj.AngularDamp;
			_wasAdjustingObjectDamping = true;
		}

		// temporarily reduced damp = responsive
		obj.LinearDamp = 0.3f;
		obj.AngularDamp = 12f;
	}

	private void RestoreObjectDamping(RigidBody2D obj)
	{
		if (obj == null) return;

		if (_savedObjectLinearDamp >= 0)
			obj.LinearDamp = _savedObjectLinearDamp;

		if (_savedObjectAngularDamp >= 0)
			obj.AngularDamp = _savedObjectAngularDamp;

		_savedObjectLinearDamp = -1;
		_savedObjectAngularDamp = -1;
		_wasAdjustingObjectDamping = false;
	}
}