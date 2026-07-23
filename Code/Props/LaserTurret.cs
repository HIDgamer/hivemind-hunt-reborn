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
	// Resting/scanning-center angle (degrees) — RestAngleDeg is the middle
	// of the back-and-forth scan sweep below, and what it eases back toward
	// once a chase is given up.
	[Export] public float RestAngleDeg = 0f;

	[ExportGroup("Scanning")]
	// Idle isn't a fixed stare — it's a slow searchlight sweep across this
	// many degrees to either side of RestAngleDeg, only actually noticing a
	// player who's inside the narrow sight cone (see SightHalfAngleDeg)
	// while it happens to be aimed their way, not the wide DetectionRange
	// circle in every direction at once.
	[Export] public float ScanArcDeg = 55f;
	[Export] public float ScanSpeedDegPerSec = 40f;
	// A short probe straight ahead of the CURRENT scan direction — if it's
	// already blocked this close, the sweep reverses right there instead of
	// grinding on toward (and visually/logically pointing straight into) a
	// wall it can't see past anyway.
	[Export] public float WallProbeDistance = 40f;
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

	private static readonly Color IdleConeColor = new Color(0.6f, 0.65f, 0.7f, 0.10f);
	private static readonly Color SearchingConeColor = new Color(1f, 0.6f, 0.1f, 0.16f);
	private static readonly Color LockedConeColor = new Color(1f, 0.15f, 0.1f, 0.24f);

	public override void _Ready()
	{
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");
		_sightCone = GetNodeOrNull<Polygon2D>("SightCone");
		_body = GetNodeOrNull<Sprite2D>("Body");
		_currentAimRad = Mathf.DegToRad(RestAngleDeg);
		ApplyAimVisuals();
		BuildSightCone();

		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		// IsClientSession, not IsNetworked — _Ready can run before a joining
		// client's deferred connection opens (same reasoning as Box_Big and
		// Laser's own _Ready checks), so the raw flag would read false there.
		bool isClient = networkManager != null && networkManager.IsClientSession;
		SetPhysicsProcess(!isClient);
		if (isClient)
		{
			UpdateSightCone(false);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
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
	}

	// Idle is a slow searchlight sweep, not a fixed stare: back and forth
	// across ScanArcDeg either side of RestAngleDeg, reversing early if the
	// sweep is about to point straight into a nearby wall. A player is only
	// ever noticed while they're inside the narrow sight cone the sweep is
	// CURRENTLY aimed through (see FindTargetInCone) — not anywhere within
	// DetectionRange the instant they're in line of sight, which is what
	// made this "instantly lock on" before.
	private void TickScanning(float dt)
	{
		float restRad = Mathf.DegToRad(RestAngleDeg);
		float extremeRad = restRad + Mathf.DegToRad(ScanArcDeg) * _scanDirection;

		if (IsWallProbeBlocked(_currentAimRad))
		{
			_scanDirection = -_scanDirection;
			extremeRad = restRad + Mathf.DegToRad(ScanArcDeg) * _scanDirection;
		}

		EaseAimToward(extremeRad, dt, ScanSpeedDegPerSec);

		if (Mathf.Abs(Mathf.AngleDifference(_currentAimRad, extremeRad)) <= Mathf.DegToRad(1f))
		{
			_scanDirection = -_scanDirection;
		}

		UpdateSightCone(false);

		Sam found = FindTargetInCone();
		if (found != null)
		{
			_target = found;
			_state = State.Tracking;
			_loseTrackTimer = LoseTrackTime;
		}
	}

	// A short ray straight along `aimRad` — true if something (a wall, per
	// SightBlockingMask) sits closer than WallProbeDistance in that exact
	// direction, meaning the scan shouldn't keep turning toward/through it.
	private bool IsWallProbeBlocked(float aimRad)
	{
		Vector2 dir = Vector2.Right.Rotated(aimRad);
		PhysicsDirectSpaceState2D spaceState = GetWorld2D().DirectSpaceState;
		var query = PhysicsRayQueryParameters2D.Create(GlobalPosition, GlobalPosition + dir * WallProbeDistance, SightBlockingMask);
		return spaceState.IntersectRay(query).Count > 0;
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

		var bolt = BoltScene.Instantiate<LaserBolt>();
		GetTree().CurrentScene.AddChild(bolt);
		Vector2 dir = Vector2.Right.Rotated(_currentAimRad);
		bolt.Launch(GlobalPosition + dir * MuzzleDistance, dir);

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
