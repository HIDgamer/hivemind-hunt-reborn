using Godot;

public partial class Box_Big : RigidBody2D
{
	[Export] public float DustSpeedThreshold = 10f;
	// Dust reads as "scraping along the ground" — gate it on low vertical
	// speed so a falling or bouncing box doesn't spray dust mid-air.
	[Export] public float DustMaxVerticalSpeed = 30f;
	[Export] public float StuckLinearThreshold = 2f;
	[Export] public float StuckSettleDelay = 0.1f;
	// Half the crate's side length (CollisionPolygon2D corners are at
	// +/-32, +/-32 in Box_Big.tscn) — used to find whichever face is
	// currently facing the ground after a flip.
	[Export] public float HalfExtent = 32f;

	// The four face-center points in the crate's own unrotated local frame.
	// Whichever one currently points most toward world-down is wherever the
	// crate is actually touching the ground right now.
	private Vector2[] _faceCenters;

	private CpuParticles2D _dustParticles;
	private float _stuckTimer = 0f;

	public override void _Ready()
	{
		_dustParticles = GetNodeOrNull<CpuParticles2D>("DustParticles");
		if (_dustParticles == null)
			GD.PushWarning("Box_Big: DustParticles node not found — dust effect disabled.");

		_faceCenters = new[]
		{
			new Vector2(0f, HalfExtent),
			new Vector2(HalfExtent, 0f),
			new Vector2(0f, -HalfExtent),
			new Vector2(-HalfExtent, 0f),
		};

		GravityScale = 1f;
		CustomIntegrator = false;
		LinearDamp = 0.1f;
		AngularDamp = 0.2f;
	}

	public override void _PhysicsProcess(double delta)
	{
		bool isSliding = Mathf.Abs(LinearVelocity.X) > DustSpeedThreshold
			&& Mathf.Abs(LinearVelocity.Y) < DustMaxVerticalSpeed;

		if (_dustParticles != null)
		{
			UpdateDustFacing();
			_dustParticles.Emitting = isSliding;
		}

		if (isSliding)
		{
			_stuckTimer = 0f;
		}
		else
		{
			_stuckTimer += (float)delta;

			if (_stuckTimer > StuckSettleDelay && LinearVelocity.Length() < StuckLinearThreshold)
			{
				LinearVelocity = Vector2.Zero;
				AngularVelocity = 0f;
			}
		}
	}

	// A fixed authored position only reads as "the bottom of the crate" as
	// long as the crate never rotates. Once it flips onto a side, the point
	// that used to be the bottom face is now a side face in world space, so
	// dust kept emitting from there instead of wherever the crate actually
	// touches the ground. Re-picking the face each frame — rather than
	// re-deriving a position from velocity, which is what drifted off the
	// ground before — keeps this a pure "which side is down right now" check
	// that still lets Godot's own transform hierarchy place it correctly.
	private void UpdateDustFacing()
	{
		Vector2 bestFace = _faceCenters[0];
		float bestDown = float.NegativeInfinity;

		foreach (Vector2 face in _faceCenters)
		{
			float down = face.Rotated(Rotation).Y;
			if (down > bestDown)
			{
				bestDown = down;
				bestFace = face;
			}
		}

		_dustParticles.Position = bestFace;
	}
}
