using Godot;

// Stationary sentry — its own hazard, not a wrapper around Laser.cs's
// continuous beam (that read as far too deadly for a patrolling turret).
// Fires discrete LaserBolt shots instead of a beam, aims slowly enough that
// a player who keeps moving can stay ahead of it, and only actually shoots
// while it has a clean line of sight and has caught up close enough to
// actually be aimed at you. Once it spots someone it keeps hunting them
// through brief cover — breaking line of sight resets a grace timer rather
// than instantly losing the target — but gives up and returns to Idle if
// they stay hidden long enough.
//
// Detection/aim/firing only ever runs on the server (or singleplayer) —
// same reasoning as every other server-authoritative hazard in this
// project (Laser, Box_Big's held-object physics): a single source of truth
// for "am I being shot at" rather than every peer re-deriving it locally.
public partial class LaserTurret : Node2D
{
	private enum State { Idle, Tracking }

	[ExportGroup("Detection")]
	[Export] public float DetectionRange = 420f;
	// Matches project.godot's layer_names: World=1 — what the line-of-sight
	// check treats as "blocks the shot," same convention LineOfSightSystem
	// already uses for tile occlusion.
	[Export(PropertyHint.Layers2DPhysics)] public uint SightBlockingMask = 1;
	// How long it keeps hunting a target through broken line of sight
	// before giving up and going back to Idle — the actual "hide here long
	// enough and it loses you" window.
	[Export] public float LoseTrackTime = 2.5f;

	[ExportGroup("Aiming")]
	// Deliberately slow — this is the entire dodge mechanic. A player who
	// keeps strafing can simply stay ahead of the turret's own barrel.
	// Was 70 — at close range (where a target's bearing angle swings
	// fastest per unit of movement) that made catching up within
	// AimToleranceDeg effectively impossible against any player who kept
	// moving at all, which read as the turret never firing rather than as
	// a fair dodge window.
	[Export] public float TurnSpeedDegPerSec = 160f;
	// Must be aimed within this many degrees of the target before it's
	// allowed to fire — without this it would fire the instant it acquires
	// a target even while still mid-swing toward them.
	[Export] public float AimToleranceDeg = 15f;
	// Resting/scanning-center angle in degrees — a small fine-tune OFFSET
	// added to this node's own placed Rotation (see _Ready/TickScanning),
	// not an absolute world angle. Previously this WAS treated as an
	// absolute world angle, which meant a turret rotated to mount on a side
	// wall/ceiling/floor still scanned centered on world +X by default,
	// requiring per-instance manual re-tuning that evidently wasn't
	// happening — now a level designer just rotates the turret node to
	// face away from its mounting surface and the scan follows automatically.
	//
	// Default is -90 (not 0): Rotation=0 means an UNROTATED marker tile,
	// i.e. a turret placed normally sitting on the floor — its mounting
	// surface is BELOW it, so it should scan centered facing UP (away from
	// the floor), sweeping west-north-east. Vector2.Right.Rotated(0) is
	// East, not up, so leaving this at 0 scanned north-east-south instead
	// (clipping straight through the floor on one side of the sweep) — a
	// -90 offset rotates the East-facing default back up to North. Every
	// other mounting orientation just falls out of this same constant once
	// ObjectManager sets Rotation from the tile's placed transform (e.g.
	// upside-down on a ceiling = Rotation 180, so center lands on South,
	// correctly scanning away from the ceiling instead).
	[Export] public float RestAngleDeg = -90f;

	[ExportGroup("Scanning")]
	// Idle isn't a fixed stare — it's a slow searchlight sweep across this
	// many degrees to either side of RestAngleDeg, only actually noticing a
	// player who's inside the narrow sight cone (see SightHalfAngleDeg)
	// while it happens to be aimed their way, not the wide DetectionRange
	// circle in every direction at once. Raised from 55 (110 total) to 90
	// (180 total) — a turret should be able to cover the full half-plane in
	// front of wherever it's mounted, not a narrow forward slice.
	[Export] public float ScanArcDeg = 90f;
	[Export] public float ScanSpeedDegPerSec = 40f;
	// Detection cone half-width, shared with the visible sight-cone polygon
	// (BuildSightCone) so what you SEE lines up exactly with what can
	// actually notice you — a wider cosmetic cone than the real detection
	// arc would be a visual lie.
	[Export] public float SightHalfAngleDeg = 16f;

	[ExportGroup("Firing")]
	[Export] public PackedScene BoltScene;
	[Export] public float FireCooldown = 1.1f;
	[Export] public float MuzzleDistance = 16f;
	[Export] public AudioStream FireSound;
	// A locked-on shot no longer always lands true — a random offset in
	// [-AimErrorDeg, AimErrorDeg] is added to the bolt's actual launch
	// direction (not the AimToleranceDeg gate above, which stays about WHEN
	// it's allowed to fire). Every fired shot being a guaranteed hit was
	// "too deadly" for a patrolling hazard. Plain export for now — a future
	// difficulty setting can scale this, not wired to one yet.
	[Export] public float AimErrorDeg = 10f;

	[ExportGroup("Power")]
	// A turret in a dead/unpowered section (see PowerRerouteStation) starts
	// dark and inert until re-powered — same "receive powered state"
	// contract Door.cs/GasVent.cs already use, so a power-reroute puzzle can
	// drive a turret exactly like it drives a light or a door.
	[Export] public bool StartPowered = true;

	private bool _powered = true;
	private AudioStreamPlayer2D _audioPlayer;
	private Polygon2D _sightCone;
	private Sprite2D _body;
	private State _state = State.Idle;
	private Sam _target;
	// Radians, kept wrapped to (-PI, PI] every update — this is the aim
	// direction used for firing/sight-cone/flip. Deliberately NOT the same
	// as this node's own Rotation: the turret's sprite must never visibly
	// spin (see _body.FlipH below), only the logical aim does.
	private float _currentAimRad;
	private float _loseTrackTimer;
	private float _cooldownTimer;
	// +1 or -1 — which way the idle scan sweep is currently turning.
	private int _scanDirection = 1;
	// _scanDirection at the moment a target was acquired — restored verbatim
	// in GiveUpTarget so the sweep genuinely resumes the pattern it was
	// already in, instead of a fresh guess computed from wherever Tracking
	// happened to leave the aim (which is what still produced back-and-forth
	// ping-ponging: that guess and the wall-probe's own re-check on the very
	// next tick could each pick a different direction and immediately
	// contradict one another).
	private int _savedScanDirection = 1;
	// Rising-edge latch for the "reached the extreme" reversal — without it,
	// this fired on every frame spent inside the 1° window (one frame's
	// step at slow ScanSpeedDegPerSec can be smaller than that), flipping
	// _scanDirection repeatedly before the aim ever actually got away from
	// the extreme, which read as the turret jittering in place by a
	// fraction of a degree instead of actually sweeping.
	private bool _reachedExtremeLatch;
	private MultiplayerSpawner _boltSpawner;
	private NetworkManager _networkManager;
	private readonly RandomNumberGenerator _rng = new();

	private static readonly Color IdleConeColor = new Color(0.6f, 0.65f, 0.7f, 0.10f);
	private static readonly Color SearchingConeColor = new Color(1f, 0.6f, 0.1f, 0.16f);
	private static readonly Color LockedConeColor = new Color(1f, 0.15f, 0.1f, 0.24f);

	public override void _Ready()
	{
		_rng.Randomize();
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");
		_sightCone = GetNodeOrNull<Polygon2D>("SightCone");
		_body = GetNodeOrNull<Sprite2D>("Body");
		_currentAimRad = Rotation + Mathf.DegToRad(RestAngleDeg);
		ApplyAimVisuals();
		BuildSightCone();

		_powered = StartPowered;
		if (_sightCone != null) _sightCone.Visible = _powered;

		_networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		// IsClientSession, not IsNetworked — _Ready can run before a joining
		// client's deferred connection opens (same reasoning as Box_Big and
		// Laser's own _Ready checks), so the raw flag would read false there.
		bool isClient = _networkManager != null && _networkManager.IsClientSession;
		SetPhysicsProcess(!isClient);
		if (isClient)
		{
			UpdateSightCone(false);
		}

		// Known multiplayer gap this closes: bolts used to be instantiated
		// directly (only ever on the server, since Fire() only ever runs
		// there), so they only ever existed in the server's own scene tree —
		// invisible on every client. A MultiplayerSpawner with spawn-data
		// (origin+direction) delivered atomically avoids the exact "spawns
		// at (0,0) before real data arrives" bug PlayerSpawner's own comment
		// already documents for plain AddSpawnableScene-style spawning.
		_boltSpawner = GetNodeOrNull<MultiplayerSpawner>("MultiplayerSpawner");
		if (_boltSpawner != null)
		{
			_boltSpawner.SpawnPath = GetTree().CurrentScene.GetPath();
			_boltSpawner.SpawnFunction = Callable.From<Variant, Node2D>(SpawnBoltFromData);
		}
	}

	private Node2D SpawnBoltFromData(Variant data)
	{
		var dict = data.AsGodotDictionary();
		LaserBolt bolt = BoltScene.Instantiate<LaserBolt>();
		bolt.Launch(dict["origin"].AsVector2(), dict["dir"].AsVector2());
		return bolt;
	}

	// Generic "receive powered state" contract — same method name Door.cs/
	// GasVent.cs already use, so a power-reroute puzzle can drive a turret
	// exactly like it drives a door or a vent. Powering off drops any
	// current target and hides the sight cone rather than leaving it aimed
	// and glowing at whatever it last saw.
	public void Powered(bool active)
	{
		_powered = active;
		if (!_powered)
		{
			_target = null;
			_state = State.Idle;
		}
		if (_sightCone != null) _sightCone.Visible = _powered;
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_powered) return;

		float dt = (float)delta;
		_cooldownTimer -= dt;

		if (_state == State.Idle)
		{
			TickScanning(dt);
			if (_state == State.Idle) return; // still scanning, nothing acquired this frame
		}

		if (!IsInstanceValid(_target))
		{
			GiveUpTarget();
			return;
		}

		bool hasLineOfSight = HasLineOfSight(_target.GlobalPosition);
		bool inRange = GlobalPosition.DistanceSquaredTo(_target.GlobalPosition) <= DetectionRange * DetectionRange;

		if (hasLineOfSight && inRange)
		{
			_loseTrackTimer = LoseTrackTime;
		}
		else
		{
			_loseTrackTimer -= dt;
			if (_loseTrackTimer <= 0f)
			{
				GiveUpTarget();
				return;
			}
		}

		float targetRad = (_target.GlobalPosition - GlobalPosition).Angle();
		EaseAimToward(targetRad, dt, TurnSpeedDegPerSec);
		UpdateSightCone(hasLineOfSight && inRange);

		float aimErrorRad = Mathf.Abs(Mathf.AngleDifference(_currentAimRad, targetRad));

		if (hasLineOfSight && inRange && _cooldownTimer <= 0f && aimErrorRad <= Mathf.DegToRad(AimToleranceDeg))
		{
			Fire();
			_cooldownTimer = FireCooldown;
		}
	}

	private void GiveUpTarget()
	{
		_target = null;
		_state = State.Idle;
		UpdateSightCone(false);

		// Restore the exact direction the sweep was already going in before
		// this target was ever acquired, rather than guessing a fresh one
		// from wherever Tracking happened to leave the aim.
		_scanDirection = _savedScanDirection;

		// _currentAimRad was left wherever the target last was, which could
		// coincidentally already be within the "reached extreme" window.
		// Seeding this from what's actually true RIGHT NOW — instead of
		// unconditionally false — means the very first TickScanning call
		// after resuming sees this as "already known," not a fresh rising
		// edge, so it can't immediately flip the direction right back out
		// from under the one just restored above.
		float restRad = Rotation + Mathf.DegToRad(RestAngleDeg);
		float extremeRad = restRad + Mathf.DegToRad(ScanArcDeg) * _scanDirection;
		_reachedExtremeLatch = Mathf.Abs(Mathf.AngleDifference(_currentAimRad, extremeRad)) <= Mathf.DegToRad(1f);
	}

	// Idle is a slow searchlight sweep, not a fixed stare: back and forth
	// across ScanArcDeg either side of RestAngleDeg. A player is only ever
	// noticed while they're inside the narrow sight cone the sweep is
	// CURRENTLY aimed through (see FindTargetInCone) — not anywhere within
	// DetectionRange the instant they're in line of sight, which is what
	// made this "instantly lock on" before.
	//
	// Deliberately no obstacle/wall awareness here — a probe-based early
	// reversal was tried and pulled back out. It was meant to stop the
	// sweep from grinding into a wall it can't see past, but the probe
	// could clip the turret's OWN mounting surface (a wall/floor-mounted
	// turret sits flush against it) and, combined with the direction this
	// picks after losing a target, could get stuck reversing back and
	// forth in place instead of ever resetting to a clean sweep. A fixed
	// arc that always completes its full sweep can't get stuck on geometry
	// at all, at the cost of occasionally visibly sweeping into a wall on
	// turrets mounted somewhere ScanArcDeg doesn't fully clear.
	private void TickScanning(float dt)
	{
		float restRad = Rotation + Mathf.DegToRad(RestAngleDeg);
		float extremeRad = restRad + Mathf.DegToRad(ScanArcDeg) * _scanDirection;

		EaseAimToward(extremeRad, dt, ScanSpeedDegPerSec);

		// Rising-edge only (see _reachedExtremeLatch's field comment) — at
		// slow ScanSpeedDegPerSec, one frame's step can be smaller than this
		// 1° window, so without the latch this fired on every frame spent
		// inside it instead of just once.
		bool reachedExtreme = Mathf.Abs(Mathf.AngleDifference(_currentAimRad, extremeRad)) <= Mathf.DegToRad(1f);
		if (reachedExtreme && !_reachedExtremeLatch)
		{
			_scanDirection = -_scanDirection;
		}
		_reachedExtremeLatch = reachedExtreme;

		UpdateSightCone(false);

		Sam found = FindTargetInCone();
		if (found != null)
		{
			_target = found;
			_state = State.Tracking;
			_loseTrackTimer = LoseTrackTime;
			// So GiveUpTarget can restore exactly this direction later,
			// instead of guessing a new one from wherever Tracking leaves
			// the aim.
			_savedScanDirection = _scanDirection;
		}
	}

	// Radians throughout, wrapped to (-PI, PI] after every step — degrees
	// were only ever for the exported tuning knobs (TurnSpeedDegPerSec,
	// ScanSpeedDegPerSec, AimToleranceDeg), converted once at the point of
	// use rather than round-tripped back and forth, so there's a single
	// unambiguous angle representation driving aim, firing direction, the
	// sight cone, and the sprite flip below.
	private void EaseAimToward(float targetRad, float dt, float speedDegPerSec)
	{
		float diff = Mathf.AngleDifference(_currentAimRad, targetRad);
		float maxStep = Mathf.DegToRad(speedDegPerSec) * dt;
		float wrapped = _currentAimRad + Mathf.Clamp(diff, -maxStep, maxStep);
		_currentAimRad = Mathf.Wrap(wrapped, -Mathf.Pi, Mathf.Pi);
		ApplyAimVisuals();
	}

	// The turret's own Node2D never rotates — only the logical aim angle
	// does. The body sprite just flips horizontally to face left/right (per
	// explicit ask: a visibly spinning turret read as wrong for this kind
	// of stationary sentry), and the sight cone rotates on its own to still
	// show the real aim direction/danger zone accurately.
	private void ApplyAimVisuals()
	{
		if (_body != null)
		{
			Vector2 aimDir = Vector2.Right.Rotated(_currentAimRad);
			_body.FlipH = aimDir.X < 0f;
		}
		if (_sightCone != null)
		{
			// _currentAimRad is an absolute world-space angle, but SightCone
			// is a child of this node — its Rotation is LOCAL, so it also
			// inherits whatever rotation the level designer applied to the
			// turret itself (e.g. mounting it sideways on a wall). Without
			// subtracting that back out here, a rotated turret's cone ends
			// up pointing at (parent rotation + aim angle) instead of just
			// the aim angle, which is exactly what made the cone inaccurate
			// on any turret that wasn't placed at Rotation 0.
			_sightCone.Rotation = _currentAimRad - Rotation;
		}
	}

	// Only a player currently inside the sight cone the scan is aimed
	// through counts — anywhere else within DetectionRange doesn't, even
	// with clear line of sight, since the turret isn't looking that way.
	private Sam FindTargetInCone()
	{
		Sam best = null;
		float bestDistSq = DetectionRange * DetectionRange;
		float halfAngleRad = Mathf.DegToRad(SightHalfAngleDeg);

		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is not Sam sam) continue;
			float distSq = GlobalPosition.DistanceSquaredTo(sam.GlobalPosition);
			if (distSq > bestDistSq) continue;

			float bearingRad = (sam.GlobalPosition - GlobalPosition).Angle();
			if (Mathf.Abs(Mathf.AngleDifference(_currentAimRad, bearingRad)) > halfAngleRad) continue;

			if (!HasLineOfSight(sam.GlobalPosition)) continue;

			bestDistSq = distSq;
			best = sam;
		}
		return best;
	}

	private bool HasLineOfSight(Vector2 targetPosition)
	{
		PhysicsDirectSpaceState2D spaceState = GetWorld2D().DirectSpaceState;
		var query = PhysicsRayQueryParameters2D.Create(GlobalPosition, targetPosition, SightBlockingMask);
		var result = spaceState.IntersectRay(query);
		return result.Count == 0;
	}

	private void Fire()
	{
		if (BoltScene == null) return;

		Vector2 dir = Vector2.Right.Rotated(_currentAimRad);
		Vector2 origin = GlobalPosition + dir * MuzzleDistance;
		// The muzzle position/visual aim stays true — only the actual fired
		// bolt's direction gets the error, so the turret still visibly aims
		// where it's aiming, it just doesn't always land.
		Vector2 firedDir = AimErrorDeg > 0f
			? dir.Rotated(Mathf.DegToRad(_rng.RandfRange(-AimErrorDeg, AimErrorDeg)))
			: dir;

		// Fire() only ever runs on the server/singleplayer already (client
		// copies have _PhysicsProcess disabled in _Ready), so no authority
		// check is needed here — only whether a spawner exists to use.
		if (_networkManager != null && _networkManager.IsNetworked && _boltSpawner != null)
		{
			var data = new Godot.Collections.Dictionary
			{
				{ "origin", origin },
				{ "dir", firedDir },
			};
			_boltSpawner.Spawn(data);
		}
		else
		{
			var bolt = BoltScene.Instantiate<LaserBolt>();
			GetTree().CurrentScene.AddChild(bolt);
			bolt.Launch(origin, firedDir);
		}

		if (_audioPlayer != null && FireSound != null)
		{
			_audioPlayer.Stream = FireSound;
			_audioPlayer.Play();
		}
	}

	// A static fan-shaped polygon pointing along the cone's own local +X —
	// ApplyAimVisuals sets the cone's Rotation directly every aim update
	// (the turret's own root Node2D never rotates, see ApplyAimVisuals), so
	// this shape only needs building once and never rebuilt per frame.
	private void BuildSightCone()
	{
		if (_sightCone == null) return;

		const int segments = 12;
		var points = new Vector2[segments + 2];
		points[0] = Vector2.Zero;
		for (int i = 0; i <= segments; i++)
		{
			float t = (float)i / segments;
			float angleDeg = Mathf.Lerp(-SightHalfAngleDeg, SightHalfAngleDeg, t);
			points[i + 1] = new Vector2(DetectionRange, 0).Rotated(Mathf.DegToRad(angleDeg));
		}
		_sightCone.Polygon = points;
	}

	// The cone itself is always visible at a low idle alpha — the whole
	// point is letting a player see the danger zone before walking into it,
	// not just after. Color/alpha communicate state: dim grey (idle/
	// scanning), amber (tracking through broken line of sight — you're
	// still being hunted, hide longer), red (locked on with a clean shot).
	private void UpdateSightCone(bool hasClearShot)
	{
		if (_sightCone == null) return;

		if (_state == State.Idle)
		{
			_sightCone.Color = IdleConeColor;
			return;
		}

		_sightCone.Color = hasClearShot ? LockedConeColor : SearchingConeColor;
	}
}
