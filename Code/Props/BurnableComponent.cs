using Godot;

// Attached to wooden props (crates, etc.) so lasers can set them alight.
// Accumulates burn time while a fire source is in contact; once it crosses
// BurnDuration the prop is destroyed. RespawnAfterBurn exists specifically
// for the tutorial level, where the crate is needed for a repeatable puzzle
// step rather than a one-time obstacle — everywhere else a burnt crate
// should just stay gone.
//
// Feedback while burning: flame licks + smoke rise off the prop and its
// sprite chars toward black as the burn timer fills, then an ember burst
// pops at the moment it's destroyed. The effect nodes are built in code so
// any scene can add this component without wiring particle children by hand.
public partial class BurnableComponent : Node
{
	[Export] public float BurnDuration = 1.2f;
	[Export] public bool RespawnAfterBurn = false;
	[Export] public float RespawnDelay = 3.0f;

	[Signal] public delegate void BurnedEventHandler();

	// How long the flames keep licking after the last ApplyBurn tick — the
	// laser calls in every physics frame while overlapping, so this only
	// needs to bridge the gap between ticks (plus a beat so the fire doesn't
	// strobe off the instant the beam cycles).
	private const float HeatLingerTime = 0.25f;

	private Node2D _target;
	private RigidBody2D _targetBody;
	private CpuParticles2D _fireParticles;
	private CpuParticles2D _smokeParticles;
	private float _burnTimer = 0f;
	private float _heatTimer = 0f;
	private bool _burned = false;
	private Vector2 _spawnPosition;
	private float _spawnRotation;
	// Throttles the burn-progress mirror stream (see ApplyBurn).
	private int _lastSentProgressBucket = -1;

	// Burning is server-authoritative in a networked session — each peer's
	// laser overlap can't be trusted to agree (beam phases only align to
	// within a network tick), so only the server accumulates burn and it
	// tells everyone else what to show.
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

	private static readonly Color ScorchedTint = new Color(0.25f, 0.18f, 0.15f, 1f);

	public override void _Ready()
	{
		_target = GetParent<Node2D>();
		_targetBody = _target as RigidBody2D;
		_spawnPosition = _target.Position;
		_spawnRotation = _target.Rotation;
		BuildBurnEffects();
	}

	public override void _Process(double delta)
	{
		if (_burned) return;

		bool heating = _heatTimer > 0f;
		if (heating)
		{
			_heatTimer -= (float)delta;
		}
		if (_fireParticles != null && _fireParticles.Emitting != heating)
		{
			_fireParticles.Emitting = heating;
			_smokeParticles.Emitting = heating;
		}

		// Char progressively toward scorched-black as the burn accumulates,
		// so a half-burnt crate visibly reads as damaged even if the beam
		// cycles off before finishing the job.
		float progress = Mathf.Clamp(_burnTimer / BurnDuration, 0f, 1f);
		_target.Modulate = Colors.White.Lerp(ScorchedTint, progress);
	}

	// Called once per physics tick while a fire source (a laser beam, etc.)
	// is actually overlapping this prop — accumulates toward BurnDuration
	// rather than igniting instantly, so a brief graze doesn't destroy it.
	public void ApplyBurn(float deltaTime)
	{
		if (_burned) return;
		// Clients' lasers don't burn — the server's do, and it mirrors the
		// progress/outcome to everyone below.
		if (IsNetClient()) return;

		_heatTimer = HeatLingerTime;
		_burnTimer += deltaTime;

		if (IsNetServer())
		{
			// Mirror charring/fire to clients, throttled to progress steps
			// of 10% instead of every physics tick.
			int bucket = (int)(Mathf.Clamp(_burnTimer / BurnDuration, 0f, 1f) * 10f);
			if (bucket != _lastSentProgressBucket)
			{
				_lastSentProgressBucket = bucket;
				Rpc(MethodName.RemoteBurnProgress, _burnTimer);
			}
		}

		if (_burnTimer >= BurnDuration)
		{
			Burn();
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void RemoteBurnProgress(float burnTimer)
	{
		if (_burned) return;
		_burnTimer = burnTimer;
		_heatTimer = HeatLingerTime;
	}

	private void Burn()
	{
		if (IsNetServer())
		{
			Rpc(MethodName.RemoteBurn);
		}
		PerformBurn();
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteBurn()
	{
		PerformBurn();
	}

	private void PerformBurn()
	{
		if (_burned) return;
		_burned = true;
		EmitSignal(SignalName.Burned);

		_fireParticles.Emitting = false;
		_smokeParticles.Emitting = false;
		SpawnBurnBurst();

		_target.Visible = false;
		if (_targetBody != null)
		{
			_targetBody.SetDeferred(RigidBody2D.PropertyName.Freeze, true);
			foreach (Node child in _targetBody.GetChildren())
			{
				if (child is CollisionShape2D shape) shape.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
			}
		}

		if (RespawnAfterBurn)
		{
			// Server (or singleplayer) owns the respawn clock; clients get
			// told when it actually happens.
			if (!IsNetClient())
			{
				GetTree().CreateTimer(RespawnDelay).Timeout += Respawn;
			}
		}
		else
		{
			GetTree().CreateTimer(0.1).Timeout += _target.QueueFree;
		}
	}

	private void Respawn()
	{
		if (IsNetServer())
		{
			Rpc(MethodName.RemoteRespawn);
		}
		PerformRespawn();
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteRespawn()
	{
		PerformRespawn();
	}

	private void PerformRespawn()
	{
		if (!IsInstanceValid(_target)) return;

		_target.Position = _spawnPosition;
		_target.Rotation = _spawnRotation;
		_target.Visible = true;
		_target.Modulate = Colors.White;
		if (_targetBody != null)
		{
			_targetBody.LinearVelocity = Vector2.Zero;
			_targetBody.AngularVelocity = 0f;
			foreach (Node child in _targetBody.GetChildren())
			{
				if (child is CollisionShape2D shape) shape.SetDeferred(CollisionShape2D.PropertyName.Disabled, false);
			}
			// Client puppets stay frozen (kinematic mirrors of the server's
			// body — see Box_Big) — unfreezing them would fork the sim again.
			if (!IsNetClient())
			{
				_targetBody.SetDeferred(RigidBody2D.PropertyName.Freeze, false);
			}
		}

		_burnTimer = 0f;
		_heatTimer = 0f;
		_lastSentProgressBucket = -1;
		_burned = false;
	}

	// ── effect construction ──────────────────────────────────────────────
	// Sizing note: the FX textures are 512px, so scale_amount stays tiny
	// (0.008-0.04) to keep everything well under a 32px tile — see the
	// established particle-scale convention for this project.

	private void BuildBurnEffects()
	{
		var additive = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };

		var fireRamp = new Gradient();
		fireRamp.SetColor(0, new Color(1.3f, 0.95f, 0.45f, 1f));
		fireRamp.SetColor(1, new Color(0.35f, 0.06f, 0.02f, 0f));
		fireRamp.AddPoint(0.45f, new Color(1f, 0.4f, 0.08f, 0.9f));

		_fireParticles = new CpuParticles2D
		{
			Name = "BurnFireParticles",
			Emitting = false,
			Amount = 16,
			Lifetime = 0.5f,
			Randomness = 0.4f,
			Texture = GD.Load<Texture2D>("res://Assets/FX/particles/alpha/muzzle_03_a.png"),
			Material = additive,
			EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
			EmissionRectExtents = new Vector2(26f, 6f),
			Position = new Vector2(0f, -18f),
			Direction = new Vector2(0f, -1f),
			Spread = 16f,
			Gravity = new Vector2(0f, -70f),
			InitialVelocityMin = 10f,
			InitialVelocityMax = 26f,
			ScaleAmountMin = 0.018f,
			ScaleAmountMax = 0.034f,
			ColorRamp = fireRamp,
		};

		var smokeRamp = new Gradient();
		smokeRamp.SetColor(0, new Color(0.16f, 0.15f, 0.14f, 0.55f));
		smokeRamp.SetColor(1, new Color(0.08f, 0.08f, 0.08f, 0f));

		_smokeParticles = new CpuParticles2D
		{
			Name = "BurnSmokeParticles",
			Emitting = false,
			Amount = 7,
			Lifetime = 1.4f,
			Randomness = 0.6f,
			Texture = GD.Load<Texture2D>("res://Assets/FX/generated/noise_wisp.png"),
			EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
			EmissionRectExtents = new Vector2(20f, 4f),
			Position = new Vector2(0f, -30f),
			Direction = new Vector2(0f, -1f),
			Spread = 20f,
			Gravity = new Vector2(0f, -35f),
			InitialVelocityMin = 6f,
			InitialVelocityMax = 14f,
			AngularVelocityMin = -20f,
			AngularVelocityMax = 20f,
			ScaleAmountMin = 0.04f,
			ScaleAmountMax = 0.07f,
			ColorRamp = smokeRamp,
		};

		// Deferred: this runs inside _Ready, i.e. while the parent prop is
		// still mid-setup of its own children — an immediate AddChild fails
		// outright there ("Parent node is busy setting up children"), which
		// silently left the burn with no fire or smoke at all.
		_target.CallDeferred(Node.MethodName.AddChild, _fireParticles);
		_target.CallDeferred(Node.MethodName.AddChild, _smokeParticles);
	}

	// One-shot ember pop at the moment of destruction. Parented to the
	// LEVEL, not the prop — the prop goes invisible (and possibly freed)
	// the same instant, which would hide/kill any child effect with it.
	private void SpawnBurnBurst()
	{
		var emberRamp = new Gradient();
		emberRamp.SetColor(0, new Color(1.2f, 0.7f, 0.25f, 1f));
		emberRamp.SetColor(1, new Color(0.4f, 0.08f, 0.02f, 0f));

		var burst = new CpuParticles2D
		{
			Emitting = true,
			OneShot = true,
			Explosiveness = 1f,
			Amount = 24,
			Lifetime = 0.6f,
			Randomness = 0.4f,
			Texture = GD.Load<Texture2D>("res://Assets/FX/particles/alpha/dirt_02_a.png"),
			Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
			EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
			EmissionRectExtents = new Vector2(24f, 24f),
			Spread = 180f,
			Gravity = new Vector2(0f, 150f),
			InitialVelocityMin = 35f,
			InitialVelocityMax = 85f,
			ScaleAmountMin = 0.008f,
			ScaleAmountMax = 0.016f,
			ColorRamp = emberRamp,
			GlobalPosition = _target.GlobalPosition,
		};
		_target.GetParent().AddChild(burst);
		GetTree().CreateTimer(1.5).Timeout += () =>
		{
			if (IsInstanceValid(burst)) burst.QueueFree();
		};
	}
}
