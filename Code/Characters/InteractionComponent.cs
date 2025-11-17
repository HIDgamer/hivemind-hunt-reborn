using Godot;

public partial class InteractionComponent : Node2D
{
	// Objects in this group (e.g. a rolling wheel) keep their normal angular
	// damping instead of the higher HeldAngularDamp applied to held objects
	// below — they're meant to spin freely as they're pushed, unlike a
	// crate that should resist tumbling in your grip.
	private const string RollingGroup = "rolling";

	[ExportGroup("Grip")]
	[Export] public float GrabMaxYDifference = 25f;
	[Export] public float MaintainMaxDistance = 80f;
	[Export] public float MaintainMaxYDifference = 30f;

	[ExportGroup("Movement")]
	[Export] public float PushSpeed = 55f;
	[Export] public float PullSpeedMultiplier = 0.6f;
	// PushWindup is Sam reaching/bracing against the object before it
	// actually starts moving — the object (and Sam, via CurrentTargetVelocityX
	// reading 0) stays put for this long after a fresh push begins, then
	// normal push velocity kicks in. Without this, the windup animation was
	// purely cosmetic: velocity was already being applied the instant the
	// grab happened, so the object started sliding before the "getting
	// ready to push" pose had any chance to read as anything but decoration.
	[Export] public float PushWindupDuration = 0.22f;
	// A RigidBody2D's actual per-tick displacement always falls a little
	// short of whatever velocity we hand it (linear damp + floor friction
	// eat into it before the physics step resolves), while Sam's kinematic
	// velocity is applied exactly as set. That gap is invisible while
	// pushing — she just bumps gently into whatever she's pushing — but
	// while pulling nothing pulls the lagging object back, so the gap grows
	// every frame until grip breaks. GrabOffsetCorrectionGain adds a small
	// catch-up term proportional to how far the object has drifted from the
	// exact offset it had at the moment it was grabbed.
	[Export] public float GrabOffsetCorrectionGain = 6f;
	[Export] public float GrabOffsetCorrectionMaxSpeed = 90f;

	[ExportGroup("Physics Tuning")]
	[Export] public float HeldLinearDamp = 0.5f;
	[Export] public float HeldAngularDamp = 4f;

	private Area2D _grabArea;
	private RigidBody2D _objectInRange;
	private RigidBody2D _currentPushable;
	private float _grabOffsetX;

	private float _savedObjectLinearDamp = -1f;
	private float _savedObjectAngularDamp = -1f;
	private bool _wasAdjustingObjectDamping = false;
	private bool _wasPushingLastFrame = false;
	private float _pushWindupTimer = 0f;

	public bool IsInteracting => _currentPushable != null;
	public RigidBody2D CurrentPushable => _currentPushable;
	public bool IsPushing { get; private set; }
	// True for the first PushWindupDuration seconds of a fresh push — Sam.cs
	// reads this to pick the PushWindup clip over the Push loop, so the
	// animation and the physics gate agree on when the actual push begins
	// instead of tracking the windup phase separately in two places.
	public bool IsWindingUpPush => IsPushing && _pushWindupTimer > 0f;
	// The exact horizontal velocity currently being driven into the held
	// object. Sam.cs mirrors this for her own movement so the player and
	// the object move in lockstep instead of the player's walk speed and
	// the object's push speed fighting each other (which reads as the
	// player shoving/bumping into whatever they're supposedly holding).
	public float CurrentTargetVelocityX { get; private set; }

	public override void _Ready()
	{
		_grabArea = GetNodeOrNull<Area2D>("GrabArea");
		if (_grabArea == null)
		{
			GD.PushWarning("InteractionComponent: GrabArea node not found — creating a default one.");
			_grabArea = new Area2D { Name = "GrabArea" };
			var shape = new CollisionShape2D { Shape = new CircleShape2D { Radius = 40f } };
			_grabArea.AddChild(shape);
			AddChild(_grabArea);
		}
		_grabArea.BodyEntered += OnGrabEntered;
		_grabArea.BodyExited += OnGrabExited;
	}

	private void OnGrabEntered(Node2D body)
	{
		if (body is RigidBody2D rb && rb.IsInGroup("pushable"))
		{
			_objectInRange = rb;
		}
	}

	private void OnGrabExited(Node2D body)
	{
		if (body is RigidBody2D rb && rb == _objectInRange)
		{
			_objectInRange = null;
		}
	}

	public void ProcessInteraction(Vector2 inputDir, Vector2 userGlobalPosition, float deltaTime)
	{
		// If no input, release grip immediately
		if (Mathf.Abs(inputDir.X) < 0.1f)
		{
			StopInteraction();
			return;
		}

		// If currently holding an object, maintain grip based on reasonable distance
		if (_currentPushable != null)
		{
			float distanceToObject = userGlobalPosition.DistanceTo(_currentPushable.GlobalPosition);
			float yDifference = Mathf.Abs(_currentPushable.GlobalPosition.Y - userGlobalPosition.Y);

			// Lose grip only if object moves too far away
			if (distanceToObject > MaintainMaxDistance || yDifference > MaintainMaxYDifference)
			{
				StopInteraction();
				return;
			}
		}
		else if (_objectInRange != null)
		{
			float yDifference = Mathf.Abs(_objectInRange.GlobalPosition.Y - userGlobalPosition.Y);
			if (yDifference > GrabMaxYDifference)
			{
				return;
			}
			_currentPushable = _objectInRange;
			_grabOffsetX = _currentPushable.GlobalPosition.X - userGlobalPosition.X;
		}
		else
		{
			// No object to interact with
			StopInteraction();
			return;
		}

		WakeAndTuneObjectForInteraction(_currentPushable);

		// Determine if the player is pushing or pulling based on relative positions.
		float offset = _currentPushable.GlobalPosition.X - userGlobalPosition.X;
		IsPushing = (offset > 0f && inputDir.X > 0f) || (offset < 0f && inputDir.X < 0f);

		if (IsPushing && !_wasPushingLastFrame)
		{
			// A fresh push just started (including switching straight from
			// pulling to pushing without releasing Interact) — hold off on
			// actually moving the object until the windup plays out.
			_pushWindupTimer = PushWindupDuration;
		}
		else if (!IsPushing)
		{
			_pushWindupTimer = 0f;
		}
		_wasPushingLastFrame = IsPushing;

		if (IsPushing && _pushWindupTimer > 0f)
		{
			_pushWindupTimer -= deltaTime;
			CurrentTargetVelocityX = 0f;
			_currentPushable.LinearVelocity = new Vector2(0f, _currentPushable.LinearVelocity.Y);
			return;
		}

		float dir = Mathf.Sign(inputDir.X);
		float inputMagnitude = Mathf.Abs(inputDir.X);
		float speed = IsPushing ? PushSpeed : PushSpeed * PullSpeedMultiplier;
		float baseVelocity = dir * speed * inputMagnitude;

		// Catch-up term: pull the object back toward the exact offset it had
		// when grabbed, so damping/friction lag doesn't silently widen the
		// gap between Sam and whatever she's holding. This correction is
		// applied only to the object's own velocity below — Sam mirrors
		// baseVelocity alone. Feeding the correction into Sam's velocity too
		// used to create a runaway loop: Sam speeding up widened the real
		// gap, which increased the correction, which sped Sam up further,
		// compounding every frame instead of settling.
		float idealX = userGlobalPosition.X + _grabOffsetX;
		float positionError = idealX - _currentPushable.GlobalPosition.X;
		float correction = Mathf.Clamp(positionError * GrabOffsetCorrectionGain, -GrabOffsetCorrectionMaxSpeed, GrabOffsetCorrectionMaxSpeed);

		CurrentTargetVelocityX = baseVelocity;
		float objectVelocityX = baseVelocity + correction;

		// Directly set the object's horizontal velocity while preserving its vertical velocity.
		// Rotation is left entirely alone here — a flipped/tumbled object
		// stays flipped while pushed rather than snapping upright. Dust and
		// other child effects already track the object's live transform
		// (position + rotation) automatically since they're regular child
		// nodes, so no separate "reposition the particles" step is needed.
		_currentPushable.LinearVelocity = new Vector2(objectVelocityX, _currentPushable.LinearVelocity.Y);
	}

	public void StopInteraction()
	{
		if (_wasAdjustingObjectDamping && _currentPushable != null)
		{
			RestoreObjectDamping(_currentPushable);
		}
		_currentPushable = null;
		IsPushing = false;
		CurrentTargetVelocityX = 0f;
		_wasAdjustingObjectDamping = false;
		_wasPushingLastFrame = false;
		_pushWindupTimer = 0f;
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

		obj.LinearDamp = HeldLinearDamp;
		if (!obj.IsInGroup(RollingGroup))
		{
			obj.AngularDamp = HeldAngularDamp;

			// Forcing a RigidBody2D's horizontal velocity every frame while
			// it's in ground contact fights the floor-friction contact
			// solver, which reacts with its own torque impulse (the classic
			// "shove a crate and it wants to tip" artifact) during that same
			// physics step — after any script-side AngularVelocity reset,
			// which is why zeroing it per-frame didn't fully stop the spin.
			// LockRotation tells the physics engine itself to never change
			// this body's rotation for any reason while held, which a
			// velocity reset can't guarantee. Wheels (RollingGroup) are
			// exempt since spinning as they're pushed is the whole point.
			obj.LockRotation = true;
		}
	}

	private void RestoreObjectDamping(RigidBody2D obj)
	{
		if (obj == null) return;

		if (_savedObjectLinearDamp >= 0) obj.LinearDamp = _savedObjectLinearDamp;
		if (_savedObjectAngularDamp >= 0) obj.AngularDamp = _savedObjectAngularDamp;
		obj.LockRotation = false;

		_savedObjectLinearDamp = -1;
		_savedObjectAngularDamp = -1;
		_wasAdjustingObjectDamping = false;
	}
}
