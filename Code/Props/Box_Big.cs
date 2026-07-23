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

	// ── networking ────────────────────────────────────────────────────────
	// Server-authoritative physics: only the server simulates this body.
	// Client copies freeze (kinematic) and mirror the server's transform via
	// the BoxSync MultiplayerSynchronizer in Box_Big.tscn — so every peer
	// agrees where the crate is, which is what pressure plates, doors, and
	// "did the push actually move it for both of us" all hang off.
	private bool _isNetClientPuppet;
	private Vector2 _lastPuppetPosition;

	// ── cooperative hold bookkeeping ─────────────────────────────────────
	// Every player currently pushing/pulling this crate — the host's own
	// hold (LocalApplyHold) and each remote client's hold (ServerApplyHold,
	// keyed by sender id) both register here instead of writing straight to
	// LinearVelocity. That's what makes two players working together
	// actually faster than one: their requested velocities are summed
	// (capped below) rather than the last write silently clobbering the
	// other's the way a bare "LinearVelocity = ..." would.
	private const float HoldLinearDamp = 0.5f;
	private const float HoldAngularDamp = 4f;
	private const float HoldTimeout = 0.25f;
	// Two people pushing the same direction should clearly beat one, but
	// still be a bounded team effort rather than unbounded — capped relative
	// to whatever the fastest single contributor actually asked for.
	private const float MaxCombinedSpeedMultiplier = 1.6f;
	// Sentinel key for the host's own local hold — always negative, so it
	// never collides with a real ENet peer id (Godot allocates those >= 1).
	private const long LocalHolderId = -1;

	private class HolderState
	{
		public float VelocityX;
		public float TimeLeft;
	}

	private readonly System.Collections.Generic.Dictionary<long, HolderState> _holders = new();
	private float _savedLinearDamp;
	private float _savedAngularDamp;

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
		// A crate this light (Godot's RigidBody2D default Mass is 1) gets
		// destabilized just by Sam's CharacterBody2D resting or walking on
		// top of it — the contact impulse from an "infinite mass" kinematic
		// body standing on a mass-1 dynamic one is enough to visibly rock or
		// launch it. Heavier + more angular resistance keeps it settled
		// underfoot while still letting a deliberate hard hit tip it over.
		Mass = 8f;
		AngularDamp = 2f;

		// IsClientSession, not IsNetworked: _Ready runs before a joining
		// client's deferred connection opens — the raw check read false
		// there, leaving client crates unfrozen and simulating their own
		// forked physics underneath the position sync.
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		_isNetClientPuppet = networkManager != null && networkManager.IsClientSession;
		if (_isNetClientPuppet)
		{
			FreezeMode = FreezeModeEnum.Kinematic;
			Freeze = true;
			_lastPuppetPosition = GlobalPosition;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		// A frozen puppet's LinearVelocity is always zero — estimate real
		// motion from the synced transform so dust still reads on clients.
		Vector2 effectiveVelocity = LinearVelocity;
		if (_isNetClientPuppet)
		{
			effectiveVelocity = (GlobalPosition - _lastPuppetPosition) / (float)delta;
			_lastPuppetPosition = GlobalPosition;
		}

		bool isSliding = Mathf.Abs(effectiveVelocity.X) > DustSpeedThreshold
			&& Mathf.Abs(effectiveVelocity.Y) < DustMaxVerticalSpeed;

		if (_dustParticles != null)
		{
			UpdateDustFacing();
			_dustParticles.Emitting = isSliding;
		}

		if (_isNetClientPuppet) return;

		ApplyHolderVelocities((float)delta);

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

	// A joining client's push/pull intent, streamed every physics tick while
	// they hold the crate (see InteractionComponent.DriveObjectVelocity).
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void ServerApplyHold(float velocityX)
	{
		if (!Multiplayer.IsServer()) return;
		RegisterHold(Multiplayer.GetRemoteSenderId(), velocityX);
	}

	// Same registration, for the host's own local push/pull — called
	// in-process (no RPC hop needed, the host already IS the physics owner)
	// from InteractionComponent.DriveObjectVelocity.
	public void LocalApplyHold(float velocityX)
	{
		RegisterHold(LocalHolderId, velocityX);
	}

	private void RegisterHold(long holderId, float velocityX)
	{
		if (_holders.Count == 0)
		{
			_savedLinearDamp = LinearDamp;
			_savedAngularDamp = AngularDamp;
			LinearDamp = HoldLinearDamp;
			AngularDamp = HoldAngularDamp;
			Rotation = Mathf.Round(Rotation / (Mathf.Pi / 2f)) * (Mathf.Pi / 2f);
			LockRotation = true;
		}

		if (!_holders.TryGetValue(holderId, out HolderState state))
		{
			state = new HolderState();
			_holders[holderId] = state;
		}
		state.VelocityX = velocityX;
		state.TimeLeft = HoldTimeout;
		Sleeping = false;
	}

	// Sums every still-active holder's requested velocity (pruning any whose
	// unreliable stream has gone quiet — a released key or a disconnect mid-
	// push), capped relative to the fastest single contributor so N helpers
	// don't scale the crate's speed without bound. Restores the crate's
	// original damping/rotation only once the LAST holder lets go — with per-
	// holder tuning instead, whichever of two simultaneous holders released
	// first would have reset damping out from under the one still pushing.
	private void ApplyHolderVelocities(float delta)
	{
		if (_holders.Count == 0) return;

		float sum = 0f;
		float maxAbsSingle = 0f;
		System.Collections.Generic.List<long> expired = null;
		foreach (var pair in _holders)
		{
			pair.Value.TimeLeft -= delta;
			if (pair.Value.TimeLeft <= 0f)
			{
				(expired ??= new System.Collections.Generic.List<long>()).Add(pair.Key);
				continue;
			}
			sum += pair.Value.VelocityX;
			maxAbsSingle = Mathf.Max(maxAbsSingle, Mathf.Abs(pair.Value.VelocityX));
		}
		if (expired != null)
		{
			foreach (long key in expired) _holders.Remove(key);
		}

		if (_holders.Count == 0)
		{
			LinearDamp = _savedLinearDamp;
			AngularDamp = _savedAngularDamp;
			LockRotation = false;
			return;
		}

		float maxCombined = maxAbsSingle * MaxCombinedSpeedMultiplier;
		float combinedX = Mathf.Clamp(sum, -maxCombined, maxCombined);
		LinearVelocity = new Vector2(combinedX, LinearVelocity.Y);
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
