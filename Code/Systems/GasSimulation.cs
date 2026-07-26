using Godot;
using System.Collections.Generic;

// Tile-based gas — the start of a proper multi-type atmospherics system
// (heat/pressure/reactions are later phases; this phase is "more than one
// gas type exists and moves independently"). Any hazard that calls EmitGas
// (see TimedHazardEmitter's EmitsGas option, or GasVent) deposits a specific
// GasType's density into the tile it's standing on; every cell holds a full
// GasMixture (all three types at once, tracked independently) rather than a
// single scalar. A sealed room naturally fills up over time — gas keeps
// re-entering from the source but has nowhere to leak out to — while an
// open area dilutes and clears, all from the same rule, no explicit "is
// this room sealed" detection needed.
//
// A single tile-based fluid model: outflow to each open neighbor is
// proportional to how much MORE gas is here than there (never flows
// "uphill" against the gradient) — a fresh concentrated pocket rushes into
// empty space fast and slows as it approaches equilibrium, which is what
// actual pressure-equalizing fluid flow looks like, without needing a full
// velocity-field/pressure-projection solver (tried that — it rendered as a
// smeared blob whose noise pattern tracked the camera instead of the room,
// wrong on both physics-look and correctness). Dropped entirely in favor of
// this simpler, reliable model.
//
// Ticks at TickInterval (~7 Hz), not every physics frame — a slow-moving
// gas cloud doesn't need 60Hz simulation to read as fluid, and this is the
// main thing keeping the whole system cheap regardless of hardware.
//
// Rendering is tile-aligned (nearest-filtered mask texture, no UV
// distortion) rather than a smoothed/blurred blob — gas should read as
// distinct, pixelated cells filling in, matching how every other tile-based
// system in this project (LineOfSightSystem's Blocky mode, the TileMap
// itself) already looks, not a painterly cloud.
//
// Purely a local rendering/hazard concern, same as LineOfSightSystem — every
// peer in a networked session simulates and draws only what's near their own
// camera, and gas exposure only ever damages the LOCAL player.
public partial class GasSimulation : Node2D
{
	public static GasSimulation Instance { get; private set; }

	public enum GasType { Flammable, Toxic, Oxygen }

	// A cell's full atmosphere — every type tracked independently rather
	// than one scalar. Reactions (a flammable+oxygen+heat-source mixture
	// igniting near something like a laser trap) are a later phase; this
	// struct is the foundation they'll hang off, since a reaction can't
	// exist without knowing what's actually mixed together in a cell.
	public struct GasMixture
	{
		public float Flammable;
		public float Toxic;
		public float Oxygen;
		// Degrees, same scale as AmbientTemperature — NOT touched by the
		// +/-/* operators below (those are pure amount arithmetic used for
		// mass bookkeeping only). Every place gas amounts actually move
		// tracks Temperature separately via an explicit mass-weighted blend
		// (see BlendTemperature), since heat doesn't add/subtract/scale
		// like a quantity of gas does — mixing 20° gas with 20° gas stays
		// 20°, it doesn't become 40°.
		public float Temperature;

		public readonly float Total => Flammable + Toxic + Oxygen;

		public float this[GasType type]
		{
			readonly get => type switch
			{
				GasType.Flammable => Flammable,
				GasType.Toxic => Toxic,
				_ => Oxygen,
			};
			set
			{
				switch (type)
				{
					case GasType.Flammable: Flammable = value; break;
					case GasType.Toxic: Toxic = value; break;
					default: Oxygen = value; break;
				}
			}
		}

		public readonly GasMixture ClampedTo(float maxTotal)
		{
			float total = Total;
			if (total <= maxTotal || total <= 0f) return this;
			GasMixture scaled = this * (maxTotal / total);
			scaled.Temperature = Temperature;
			return scaled;
		}

		public static GasMixture operator +(GasMixture a, GasMixture b) => new GasMixture
		{
			Flammable = a.Flammable + b.Flammable,
			Toxic = a.Toxic + b.Toxic,
			Oxygen = a.Oxygen + b.Oxygen,
		};

		public static GasMixture operator -(GasMixture a, GasMixture b) => new GasMixture
		{
			Flammable = a.Flammable - b.Flammable,
			Toxic = a.Toxic - b.Toxic,
			Oxygen = a.Oxygen - b.Oxygen,
		};

		public static GasMixture operator *(GasMixture a, float s) => new GasMixture
		{
			Flammable = a.Flammable * s,
			Toxic = a.Toxic * s,
			Oxygen = a.Oxygen * s,
		};
	}

	[Export] public NodePath TileMapPath = "../TileMap";
	// Real physics query, not a hardcoded TileMap layer index — a "Solid
	// layer number" silently goes stale the moment a level's TileMap layers
	// get reordered/renamed (exactly what happened here: this was pointing
	// at the Pipes layer instead of Solid), while a collision-mask query
	// always matches whatever tiles the player/turrets actually collide
	// with, regardless of which visual layer painted them.
	[Export(PropertyHint.Layers2DPhysics)] public uint SolidCollisionMask = 1;

	// Which way a scene-placed vent prop (GasVent.cs with IsPipeVent set)
	// moves gas at its tile — Intake pulls from the local room into the
	// pipe network, Output releases arriving gas back out into whatever
	// room it sits in. A vent tile nothing has registered a mode for
	// defaults to Output (see ProcessPipeNetworks), which is what every
	// vent tile did unconditionally before intake/output existed.
	public enum VentMode { Intake, Output }

	// Populated by GasVent.cs (or any future pipe-aware prop) via
	// RegisterVent/UnregisterVent — NOT by the TileMap. The Pipes layer's
	// "is_vent" custom data still decides pure NETWORK TOPOLOGY (which
	// tiles are candidate vent points, and which connected run they belong
	// to); this decides which of those candidate tiles actually have a
	// live prop on them right now and which way it's currently facing.
	private readonly Dictionary<Vector2I, VentMode> _ventModes = new();
	private readonly Dictionary<Vector2I, bool> _ventBroken = new();

	public void RegisterVent(Vector2I tile, VentMode mode) => _ventModes[tile] = mode;

	public void UnregisterVent(Vector2I tile)
	{
		_ventModes.Remove(tile);
		_ventBroken.Remove(tile);
	}

	public void SetVentBroken(Vector2I tile, bool broken) => _ventBroken[tile] = broken;

	public Vector2I WorldToTile(Vector2 worldPosition) => _tileMap.LocalToMap(_tileMap.ToLocal(worldPosition));

	// True only if tile is an is_vent-flagged Pipes-layer cell that the
	// flood fill in BuildPipeNetworks actually found as part of a connected
	// run — a scene-placed Intake/Output vent prop checks this before
	// registering, so a prop dropped somewhere with no matching pipe tile
	// underneath (or a disconnected/orphaned is_vent tile) fails loudly
	// instead of silently pretending to work.
	public bool IsPipeNetworkTile(Vector2I tile)
	{
		foreach (List<Vector2I> network in _pipeNetworks)
		{
			if (network.Contains(tile)) return true;
		}
		return false;
	}

	[ExportGroup("Piping")]
	// A dedicated TileMap layer you paint pipe tiles onto (add it in the
	// TileMap node's Layers panel) — any tile placed here is part of a pipe
	// network. Flag specific pipe tiles with the "is_vent" custom data (also
	// added to the level's TileSet) to mark them as intake/outtake points.
	//
	// Resolved BY NAME in _Ready (see ResolvePipeLayer), not used directly
	// as a raw index — a hardcoded layer number silently goes stale the
	// moment a level's TileMap layers get reordered or a new one gets
	// inserted (exactly what happened here: this drifted from actually
	// meaning "Pipes" to meaning "Props2", so every pipe tile lookup read
	// unrelated decoration tiles instead — same fragile-index bug class
	// SolidCollisionMask replaced a hardcoded SolidLayer for, earlier).
	// PipeLayer itself is kept only as a manual fallback if PipeLayerName
	// can't be found.
	[Export] public string PipeLayerName = "Pipes";
	[Export] public int PipeLayer = 4;
	[Export] public string IsVentDataName = "is_vent";
	// A second boolean custom data layer on the SAME Pipes TileSet tiles —
	// paint it on any run of pipe you want to read as unsealed/damaged
	// (as opposed to every pipe tile leaking uniformly, or none leaking at
	// all). A tile left unflagged never leaks, full stop.
	[Export] public string CanLeakDataName = "pipe_leak";
	// Fraction of an in-transit parcel's mass skimmed off into the room at
	// a network's leak tiles EACH TICK it's still traveling — a parcel
	// crossing several leaky tiles over a multi-tick trip can lose most of
	// itself before ever reaching an Output vent, same idea as a real
	// damaged pipe run never delivering full pressure to the far end.
	[Export] public float PipeLeakRate = 0.05f;
	[Export] public float VentFlowRate = 0.4f;
	// A vent doesn't just drain the one tile it's flush against — it pulls
	// from every open tile within this many steps, weighted by distance
	// (closer contributes more), which is what actually reads as "suction"
	// pulling in gas from around the room rather than only reacting once
	// gas happens to reach the exact adjacent tile.
	[Export] public int VentPullRadiusTiles = 3;
	// How many pipe-tiles of distance a parcel of gas crosses per
	// simulation tick while in transit — tuned well above the equivalent
	// ambient spread rate (see FlowRate/SettleRate) so pipes genuinely read
	// as a faster shortcut, not just an equivalent-speed detour. Unlike the
	// old instant same-tick pooling, gas now actually takes
	// networkLength/this-many ticks to cross a run rather than teleporting.
	[Export] public float PipeTravelTilesPerTick = 6f;

	[ExportGroup("Simulation")]
	[Export] public float TickInterval = 0.15f;
	// How much of a density DIFFERENCE between two neighboring tiles
	// equalizes each tick — gas never flows from a lower-density tile to a
	// higher one, and the flow rate scales with how lopsided the difference
	// is, so a concentrated pocket rushes into empty space fast and slows
	// down as it approaches an even spread. This is the actual "fluid"
	// behavior (pressure/density equalization), simpler than a full
	// velocity-field solver but still genuinely gradient-driven rather than
	// a flat fixed-fraction spread.
	[Export] public float FlowRate = 0.5f;
	// Defaults to 0 — a sealed room with no vent path out should hold onto
	// its gas indefinitely once emission stops, not quietly fade away on
	// its own. Concentration (via FlowRate/diffusion) and pipe suction are
	// the only things that should ever actually remove gas from a room;
	// this passive drain is purely an optional extra knob (e.g. a
	// deliberately "leaky" hazard that thins out on its own) rather than
	// something every gas cell suffers from by default.
	[Export] public float DecayRate = 0f;
	// Purely a floating-point/dictionary-size cleanup floor — NOT a
	// meaningful "gas amount" cutoff. A fast-settling gas cascading down an
	// open shaft (see SettleRate) legitimately passes through many tiles
	// holding only a thin, transient fraction each on its way to actually
	// pooling at the floor; if this is set too high it PERMANENTLY DELETES
	// that real, conserved mass mid-transit before it ever gets a chance to
	// pool, which reads as gas disappearing even with DecayRate at 0. Keep
	// this near-zero — it should only ever catch genuine rounding noise.
	[Export] public float MinDensity = 0.0005f;

	[ExportGroup("Heat & Buoyancy")]
	// Degrees — whatever a fresh, unheated tile settles toward once no
	// source is actively heating/cooling it, and the baseline every
	// emitter's own EmittedTemperature is measured against.
	[Export] public float AmbientTemperature = 20f;
	// "Normal room air" — how much Oxygen a tile has by default with
	// nothing ever emitted into it, returned only by GetEffectiveMixture
	// (a read-path fallback; _density itself stays untouched/sparse — see
	// that method's own comment). Kept well below DamageThreshold so
	// ordinary ambient air is never inherently hazardous by construction.
	[Export] public float AmbientOxygenLevel = 0.3f;
	// How quickly an isolated pocket's temperature relaxes back toward
	// AmbientTemperature each tick — a heat SOURCE has to keep re-heating
	// gas that's actively cooling back down the moment it stops.
	[Export] public float HeatLossRate = 0.03f;
	// How much a cell's temperature blends toward its neighbors' average
	// each tick — plain thermal conduction, independent of whether any gas
	// mass is actually flowing between them (a still, sealed pocket of hot
	// gas next to a cold one still equalizes over time).
	[Export] public float HeatConductionRate = 0.25f;
	// A cell with NO gas of any type can still be "thermally meaningful" —
	// a room a fire hazard has been heating with no gas in it at all (see
	// AddHeat) needs to actually persist/conduct/decay like any other
	// tracked cell instead of vanishing every tick just because its Total
	// is zero. This is the threshold (degrees above/below
	// AmbientTemperature) past which a zero-gas cell still counts — see
	// HasThermalMass.
	[Export] public float MinHeatAboveAmbient = 0.5f;
	// A zero-gas cell's Temperature is normally recomputed each tick as a
	// mass-weighted average of whatever moved through it — with a true
	// mass of 0 that average is 0/0, which would silently reset a heat-only
	// cell back to ambient every tick. This is the nominal minimum "weight"
	// a heat-only cell's own carried-forward temperature gets in that
	// average so it actually survives — real gas-bearing cells are
	// unaffected, their true mass already dwarfs this floor.
	[Export] public float HeatOnlyNominalMass = 0.01f;
	// Relative weight of each gas type at AmbientTemperature (1.0 = same as
	// ordinary room air) — this is what "density = how heavy a gas is"
	// actually means here: below 1 is lighter than air (tends to rise),
	// above 1 is heavier (tends to sink), independent of concentration.
	// Deliberately spread wide (light / medium / heavy) rather than
	// realistic real-world gas densities, so buoyancy testing shows an
	// obvious, unambiguous difference between all three types at a glance.
	[Export] public float FlammableWeight = 0.3f; // light — rises fast
	[Export] public float OxygenWeight = 1.0f; // medium — baseline, neither rises nor sinks
	[Export] public float ToxicWeight = 2.2f; // heavy — sinks fast, pools at the floor
	// How strongly a cell's net buoyancy (from weight AND temperature)
	// biases vertical flow beyond whatever the raw concentration gradient
	// alone would do — 0 disables buoyancy entirely (pure concentration
	// diffusion), higher makes hot/light gas rise and cold/heavy gas sink
	// more assertively even through a perfectly uniform-concentration room.
	[Export] public float BuoyancyStrength = 0.6f;
	// Fraction of a cell's gas that actively falls/rises straight toward
	// its preferred vertical neighbor each tick, capped only by how much
	// ROOM that neighbor has left (not by the concentration difference the
	// way ordinary diffusion is) — gravity doesn't care whether the floor
	// already has some gas on it, only whether there's still space. This is
	// what produces genuine liquid-like pooling: a heavy gas rushes
	// straight down and, once the floor tile is full, spreads sideways to
	// level out — a flat-topped pool instead of an even diffuse haze.
	//
	// Was 0.9 — through a tall open shaft that meant ~90% of a packet's gas
	// moved one tile further down every ~0.15s tick, cascading through many
	// tiles almost instantly and leaving only a razor-thin, easily-pruned
	// fraction at each one on the way down. Lower gives it a chance to
	// actually accumulate as it falls instead of shredding into fragments.
	[Export] public float SettleRate = 0.5f;
	// How many effective "weight units" one degree above/below
	// AmbientTemperature is worth — hotter reduces effective weight
	// (rises), colder increases it (sinks), same real-world relationship
	// as hot air ballooning vs. cold air sinking.
	[Export] public float HeatBuoyancyPerDegree = 0.01f;

	[ExportGroup("Rendering")]
	[Export] public int WindowRadiusTiles = 16;
	[Export] public Color ToxicColor = new Color(0.45f, 0.9f, 0.3f, 1f);
	[Export] public Color OxygenColor = new Color(0.55f, 0.78f, 1f, 1f);
	[Export] public float GasAlpha = 0.8f;
	// A tiny bit of per-tile shade variation so a solid block of gas isn't
	// a completely flat color — sampled per-TILE (quantized, not a smooth
	// continuous coordinate), so it can't smear across tile edges or
	// distort the mask's own crisp alignment the way a drifting noise
	// distortion of the sampling position would.
	[Export] public float MottleStrength = 0.12f;
	// Flammable has no color of its own — it's invisible, and instead
	// distorts (via screen_texture) whatever's actually behind it, like
	// real heat haze. This is how strongly (world-anchored, animated noise
	// offset into SCREEN_UV) and how fast/dense that distortion pattern is.
	[Export] public float HeatDistortionStrength = 0.018f;
	[Export] public float HeatPatternFrequency = 0.35f;
	[Export] public float HeatPatternSpeed = 1.4f;
	// Toxic's own animated "bubbling murk" pattern (unchanged shape from
	// before), plus how much brighter/more saturated than its flat
	// ToxicColor the glow reads, and how fast it pulses — an emissive,
	// "this is radioactive" read instead of a flat green fog.
	[Export] public float ToxicPatternFrequency = 0.12f;
	[Export] public float ToxicPatternSpeed = 0.35f;
	// Was 1.6 — visibly overbright/oversaturated in practice, closer to a
	// solid glowing block than a haze.
	[Export] public float ToxicGlowStrength = 1.05f;
	[Export] public float ToxicGlowPulseSpeed = 2.0f;
	// Multiplies Oxygen's rendered alpha down to near-nothing — it's still
	// real data (visible in DebugVisualization), just not something a
	// player consciously notices during normal play.
	[Export] public float OxygenVisibility = 0.05f;

	[ExportGroup("Exposure")]
	// Total density (any mix of types) above this starts hurting — reaction-
	// specific effects (e.g. Flammable igniting) are a later phase; for now
	// every gas is uniformly hazardous to breathe regardless of composition.
	[Export] public float DamageThreshold = 0.5f;
	// Base damage at exactly DamageThreshold — scales up with Pressure (see
	// below) the further past threshold a cell gets, so standing in a
	// barely-hazardous room stings but a fully pressurized sealed closet
	// actually hurts.
	[Export] public int DamagePerTick = 1;
	[Export] public float DamageTickInterval = 0.8f;

	[ExportGroup("Room Heat")]
	// Degrees — above this, standing in the room itself starts hurting,
	// independent of any gas being present (reads via GetEffectiveMixture,
	// so this correctly picks up heat-only cells too — see AddHeat).
	[Export] public float RoomHeatDamageThreshold = 45f;
	[Export] public int RoomHeatDamagePerTick = 1;
	// Paced well past HealthComponent's 0.5s invulnerability window (see
	// Sam.BurnTickInterval's own comment) so every tick actually lands.
	[Export] public float RoomHeatDamageTickInterval = 0.8f;
	// Degrees — hotter than RoomHeatDamageThreshold. Past this, the room
	// doesn't just hurt, it periodically sets the player on fire (see
	// Sam.ApplyBurning) — a real risk of actual ignition, not just heat
	// discomfort.
	[Export] public float BurningIgnitionTemperature = 70f;
	[Export] public float BurningCheckInterval = 1.0f;
	[Export] public float BurningDuration = 3f;

	[ExportGroup("Pressure")]
	// A cell's Pressure IS just its Total gas amount — but unlike before,
	// that number is no longer hard-capped at 1.0. A sealed room fed by a
	// vent with nowhere to vent to now genuinely keeps building PAST "full"
	// instead of silently discarding everything past the visual saturation
	// point, which is what real pressure buildup in a sealed space means.
	// MaxPressure is just a sane safety ceiling against runaway float
	// growth over a very long, never-vented session — in practice it's far
	// above anything DamageThreshold/BurstPressure would ever care about.
	[Export] public float MaxPressure = 20f;
	// A cell crossing this Pressure fires PressureBurst once (not every
	// tick it stays over) — nothing in GasSimulation itself reacts to this;
	// it's a hook for other hazards (a weak door, a vent cap, a wall panel)
	// to subscribe to and blow open/react to overpressure without
	// GasSimulation needing to know anything about doors.
	[Export] public float BurstPressure = 3f;
	[Signal] public delegate void PressureBurstEventHandler(Vector2I tile, Vector2 worldPosition, float pressure);

	[ExportGroup("Ignition")]
	// A trace of Flammable shouldn't combust — only a real buildup. Below
	// this, both TryIgnite (an external spark/beam) and passive autoignition
	// just do nothing to that cell. This gates STARTING a fire only — see
	// ChainPropagationMinFlammable for how far an already-burning chain
	// reaction reaches once it's under way.
	[Export] public float IgnitionMinFlammable = 0.15f;
	// Real natural gas has a wide flammability range (roughly 5-15% by
	// volume) — a spark can't start combustion in a trace concentration,
	// but once something IS burning nearby, the flame front propagates
	// through much thinner concentrations than what was needed to start
	// it. Kept far below IgnitionMinFlammable specifically so gas that's
	// settled/spread thin at floor level (see SettleRate) still gets swept
	// into an already-started chain reaction instead of the flood fill
	// stopping right at the edge of where it thinned out.
	[Export] public float ChainPropagationMinFlammable = 0.02f;
	// A flammable cell spontaneously catches once ITS OWN Temperature
	// crosses this, with no external spark needed — e.g. flammable gas
	// drifting near a hot vent eventually catches on its own. Tuned for
	// pacing, not real-world autoignition points.
	[Export] public float IgnitionTemperature = 260f;
	// Degrees added to an igniting cell's own Temperature — this is what
	// actually makes fire PROPAGATE through a connected flammable cloud:
	// the spike spreads to neighbors next tick via the existing
	// HeatConductionRate blending, and any neighbor that's still flammable
	// and now hot enough ignites too on its own tick, cascading outward
	// with no separate "spread" algorithm needed.
	[Export] public float IgnitionHeatRelease = 900f;
	// Floor values — a barely-igniting single tile still deals/reaches at
	// least this much. Real scaling comes from the Per-Flammable-Unit
	// exports below, continuous in how much gas the whole connected pocket
	// actually had, not a jump between 3 fixed sizes.
	[Export] public int IgnitionDamage = 3;
	// How far (in pixels, roughly one tile) an external ignition source
	// like TryIgnite searches from its target point by default, and also
	// the floor blast radius for a single barely-igniting tile.
	[Export] public float IgnitionRadius = 24f;
	// Extra blast radius (pixels) and damage per unit of total Flammable
	// gas the connected pocket had when it went up — unbounded, so a big
	// room's worth of gas produces a genuinely bigger blast than "Large"
	// tier ever could on its own, instead of capping out at a fixed
	// multiplier.
	[Export] public float ExplosionRadiusPerFlammableUnit = 40f;
	[Export] public float ExplosionDamagePerFlammableUnit = 2f;
	// Peak knockback strength at the blast center, falling off to 0 at the
	// edge of the blast radius. This multiplies straight into Sam's own
	// knockback pipeline (Velocity = knockbackDirection * 100, see
	// Sam.OnTookDamage) by passing a non-unit-length direction vector — no
	// changes needed there; 1.0 here means the same push as any other
	// hazard, higher means a genuinely stronger shove.
	[Export] public float ExplosionKnockbackStrength = 2.5f;
	// Impulse applied to nearby RigidBody2D props (crates, etc.), same
	// center-to-edge falloff as everything else.
	[Export] public float ExplosionPropImpulseStrength = 400f;
	// How much permanent char EnvironmentDamage.ApplyScorch adds at the
	// blast center (falls off toward the edge of the blast radius) — see
	// EnvironmentDamage.cs.
	[Export] public float ExplosionScorchAmount = 0.4f;
	// Structural damage dealt to EnvironmentDamage-tracked destructible
	// tiles at the blast center — comfortably above TileDestroyThreshold's
	// default (1.0) so a real explosion actually breaks flagged walls, not
	// just chars them.
	[Export] public float ExplosionStructuralDamage = 1.5f;
	// Safety cap on how many connected flammable tiles a single chain
	// reaction will sweep up — a pathologically large, fully-flammable room
	// still detonates (capped) rather than the flood fill blowing the frame
	// budget trying to walk every tile in it.
	[Export] public int MaxChainReactionTiles = 400;
	// Total Flammable consumed across a whole connected pocket (not a
	// single tile) picks the TIER — used only to choose which of the 3
	// authored GasExplosion.tscn sprite animations plays and its visual
	// Scale (there are only 3 sprite sheets, so VFX selection stays
	// discrete even though the actual blast radius/damage above is now
	// continuous).
	public enum ExplosionTier { Small, Medium, Large }
	[Export] public float MediumExplosionFlammable = 1.5f;
	[Export] public float LargeExplosionFlammable = 4f;
	[Export] public float MediumExplosionScaleMultiplier = 1.5f;
	[Export] public float LargeExplosionScaleMultiplier = 2.2f;
	[Signal] public delegate void GasIgnitedEventHandler(Vector2 worldPosition, ExplosionTier tier, float totalFlammableConsumed);

	[ExportGroup("Debug")]
	// Editor/Inspector checkbox — flip it live (even mid-play, via the
	// Remote scene tree) to overlay per-tile atmospherics: each cell's exact
	// Flammable/Toxic/Oxygen breakdown as text, plus the Pipes layer's
	// network topology (plain pipe tiles vs. actual is_vent intake/outtake
	// points), so a "why isn't this venting/leaking" question can be
	// answered by looking instead of guessing at numbers.
	[Export] public bool DebugVisualization = false;
	[Export] public Color DebugPipeColor = new Color(0.3f, 0.55f, 1f, 0.9f);
	[Export] public Color DebugVentColor = new Color(1f, 0.75f, 0.15f, 0.95f);
	[Export] public int DebugFontSize = 10;

	// Tile-aligned rendering: mask_texture uses filter_nearest (a hard edge
	// exactly at tile boundaries, like LineOfSightSystem's Blocky mode) and
	// the mottle is sampled from a per-tile hash of the WORLD grid
	// coordinate — never a continuously-drifting UV offset, which is what
	// previously made the whole cloud's texture appear to slide with the
	// camera instead of staying put in the room (the "moves with the
	// player" bug: the noise was sampled from screen-space UV, not the
	// world-anchored grid_uv).
	// Each gas type is a distinct MATERIAL now, not just a different hue of
	// the same fog: Flammable has NO color of its own — it's invisible, and
	// instead distorts (via screen_texture/SCREEN_UV) whatever's actually
	// behind it, real heat-haze refraction rather than a tint. Toxic reads
	// as a bright, pulsing, emissive radiation glow. Oxygen keeps the same
	// underlying alpha math as before but scaled down to near-nothing —
	// present in the data (and in DebugVisualization), not something a
	// player consciously notices. Everything stays world-anchored (never
	// screen-space UV) for both the noise patterns and the mottle, same
	// rule established earlier this session after an actual regression
	// where screen-space sampling made gas appear to drift with the camera.
	private const string ShaderSource = @"
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D mask_texture : filter_linear;
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D toxic_noise_texture : repeat_enable, filter_linear;
uniform vec2 sprite_world_size;
uniform vec2 grid_offset_world;
uniform float tile_size;
uniform float grid_span_tiles;
uniform vec4 toxic_color : source_color = vec4(0.45, 0.9, 0.3, 1.0);
uniform vec4 oxygen_color : source_color = vec4(0.55, 0.78, 1.0, 1.0);
uniform float gas_alpha = 0.8;
uniform float mottle_strength = 0.12;
uniform float heat_distortion_strength = 0.018;
uniform float heat_pattern_frequency = 0.35;
uniform float heat_pattern_speed = 1.4;
uniform float toxic_pattern_frequency = 0.12;
uniform float toxic_pattern_speed = 0.35;
uniform float toxic_glow_strength = 1.6;
uniform float toxic_glow_pulse_speed = 2.0;
uniform float oxygen_visibility = 0.05;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

// Smooth (bilinear-interpolated hash) value noise — cheap, and smooth
// enough to read as drifting wisps/bubbles/haze rather than static per-tile
// speckle the way the flat hash() mottle below reads.
float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void fragment() {
	vec2 local_world_offset = (UV - 0.5) * sprite_world_size;
	vec2 offset_from_grid_center = local_world_offset + grid_offset_world;
	vec2 tile_offset = offset_from_grid_center / tile_size;
	vec2 grid_uv = (tile_offset / grid_span_tiles) + 0.5;

	vec4 mask = vec4(0.0);
	if (grid_uv.x >= 0.0 && grid_uv.x <= 1.0 && grid_uv.y >= 0.0 && grid_uv.y <= 1.0) {
		mask = texture(mask_texture, grid_uv);
	}

	float flammable_amt = mask.r;
	float toxic_amt = mask.g;
	float oxygen_amt = mask.b;

	vec2 world_coord = offset_from_grid_center / tile_size;
	vec2 world_tile = floor(world_coord);
	float mottle = mix(1.0 - mottle_strength, 1.0 + mottle_strength, hash(world_tile));

	// ---- Flammable: no tint, heat-haze distortion of whatever's behind it.
	vec3 base_rgb = vec3(0.0);
	float base_alpha = 0.0;
	if (flammable_amt > 0.002) {
		vec2 heat_coord = world_coord * heat_pattern_frequency + vec2(TIME * heat_pattern_speed, TIME * heat_pattern_speed * 0.8);
		float heat_x = value_noise(heat_coord) - 0.5;
		float heat_y = value_noise(heat_coord + vec2(31.7, 7.3)) - 0.5;
		vec2 heat_offset = vec2(heat_x, heat_y) * heat_distortion_strength * flammable_amt;
		base_rgb = texture(screen_texture, SCREEN_UV + heat_offset).rgb;
		base_alpha = 1.0;
	}

	// ---- Toxic: glowing, pulsing radiation haze. Smooth FastNoiseLite
	// texture (same convention as Laser.cs's beam shader) instead of the
	// cheap inline hash-grid value_noise — at this frequency the hash
	// noise read as blocky/pixelated patches rather than a soft haze.
	vec2 toxic_noise_uv = world_coord * toxic_pattern_frequency
		+ vec2(TIME * toxic_pattern_speed, TIME * toxic_pattern_speed * 0.3);
	float toxic_n = texture(toxic_noise_texture, toxic_noise_uv).r;
	float bubble = smoothstep(0.3, 0.7, toxic_n);
	float pulse = 0.92 + 0.08 * sin(TIME * toxic_glow_pulse_speed + world_tile.x * 0.6 + world_tile.y * 0.6);
	vec3 toxic_rgb = toxic_color.rgb * toxic_glow_strength * mix(0.85, 1.1, bubble) * pulse;
	float toxic_alpha = toxic_amt * gas_alpha * mottle;

	// ---- Oxygen: same shape as before, scaled down to near-invisible.
	float oxygen_alpha = oxygen_amt * gas_alpha * mottle * oxygen_visibility;

	// Composite: Toxic/Oxygen tint blends OVER the (possibly heat-
	// distorted) base, so a cell that's both radioactive and hot shows both
	// effects at once instead of one clobbering the other.
	float gas_alpha_out = clamp(toxic_alpha + oxygen_alpha, 0.0, 1.0);
	vec3 gas_rgb = toxic_alpha > oxygen_alpha ? toxic_rgb : oxygen_color.rgb;
	vec3 final_rgb = mix(base_rgb, gas_rgb, gas_alpha_out);
	float final_alpha = max(base_alpha, gas_alpha_out);

	COLOR = vec4(final_rgb, clamp(final_alpha, 0.0, 1.0));
}
";

	private TileMap _tileMap;
	private Vector2I _tileSize;
	private int _pipeLayerIndex;
	private Dictionary<Vector2I, GasMixture> _density = new();

	private Sprite2D _overlay;
	private ShaderMaterial _material;
	private Image _maskImage;
	private ImageTexture _maskTexture;
	private int _maskResolution;

	private float _tickTimer;
	private float _damageTimer;
	private float _heatDamageTimer;
	private float _burningCheckTimer;
	private Vector2I _debugWindowCenterTile;

	// Built once at _Ready — index-aligned with _pipeNetworks. Each entry is
	// every vent tile belonging to one connected run of Pipes-layer tiles.
	// Static for the level's lifetime; pipes aren't expected to be
	// repainted at runtime.
	private readonly List<List<Vector2I>> _pipeNetworks = new();
	// Total pipe-cell count of each connected run (not just its vents) —
	// the proxy for physical pipe length used to compute travel time.
	private readonly List<int> _pipeNetworkLength = new();
	// In-transit gas per network, index-aligned with _pipeNetworks — real
	// parcels with a countdown, not instant same-tick teleportation.
	private readonly List<Queue<(GasMixture Mixture, int TicksRemaining)>> _pipeNetworkTransit = new();
	// CanLeakDataName-flagged pipe cells per network, index-aligned with
	// _pipeNetworks — where an in-transit parcel bleeds a little gas into
	// the room each tick it's still traveling.
	private readonly List<List<Vector2I>> _pipeNetworkLeakCells = new();

	private readonly RandomNumberGenerator _rng = new();

	private static readonly string[] IgnitionSounds =
	{
		"res://Sound/Explosions/explosion.ogg",
		"res://Sound/Explosions/explosion02.ogg",
		"res://Sound/Explosions/explosion03.ogg",
		"res://Sound/Explosions/explosion04.ogg",
		"res://Sound/Explosions/explosion05.ogg",
		"res://Sound/Explosions/explosion06.ogg",
		"res://Sound/Explosions/explosion07.ogg",
		"res://Sound/Explosions/explosion08.ogg",
		"res://Sound/Explosions/explosion09.ogg",
	};

	// Instance is set here rather than in _Ready() — Godot runs _EnterTree()
	// for an entire scene batch (parent-first, top-down) before _ready() runs
	// for ANY node in that batch, so setting it here guarantees every sibling
	// consumer's own _Ready() sees a non-null Instance regardless of scene
	// declaration order, instead of racing against this node's own _Ready().
	public override void _EnterTree()
	{
		_tileMap = GetNodeOrNull<TileMap>(TileMapPath);
		if (_tileMap != null) Instance = this;
	}

	public override void _Ready()
	{
		_rng.Randomize();
		if (_tileMap == null)
		{
			GD.PushWarning($"GasSimulation: no TileMap found at '{TileMapPath}' — disabling.");
			SetProcess(false);
			return;
		}

		_tileSize = _tileMap.TileSet.TileSize;
		_pipeLayerIndex = ResolvePipeLayer();
		BuildPipeNetworks();

		_maskResolution = WindowRadiusTiles * 2 + 1;
		_maskImage = Image.CreateEmpty(_maskResolution, _maskResolution, false, Image.Format.Rgba8);
		_maskTexture = ImageTexture.CreateFromImage(_maskImage);

		_material = new ShaderMaterial { Shader = new Shader { Code = ShaderSource } };
		_material.SetShaderParameter("mask_texture", _maskTexture);
		_material.SetShaderParameter("tile_size", (float)_tileSize.X);
		_material.SetShaderParameter("grid_span_tiles", (float)_maskResolution);
		_material.SetShaderParameter("toxic_color", ToxicColor);
		_material.SetShaderParameter("oxygen_color", OxygenColor);
		_material.SetShaderParameter("gas_alpha", GasAlpha);
		_material.SetShaderParameter("mottle_strength", MottleStrength);
		_material.SetShaderParameter("heat_distortion_strength", HeatDistortionStrength);
		_material.SetShaderParameter("heat_pattern_frequency", HeatPatternFrequency);
		_material.SetShaderParameter("heat_pattern_speed", HeatPatternSpeed);
		_material.SetShaderParameter("toxic_pattern_frequency", ToxicPatternFrequency);
		_material.SetShaderParameter("toxic_pattern_speed", ToxicPatternSpeed);
		_material.SetShaderParameter("toxic_glow_strength", ToxicGlowStrength);
		_material.SetShaderParameter("toxic_glow_pulse_speed", ToxicGlowPulseSpeed);
		_material.SetShaderParameter("oxygen_visibility", OxygenVisibility);

		// Same FastNoiseLite + NoiseTexture2D convention already used
		// elsewhere in this project (Laser.cs's beam shader) — organic,
		// smooth noise instead of the cheap inline hash/value_noise, which
		// at this frequency read as blocky/pixelated rather than a smooth
		// radioactive haze.
		var toxicNoise = new FastNoiseLite { Frequency = 0.045f, Seed = 4242 };
		var toxicNoiseTexture = new NoiseTexture2D { Width = 128, Height = 128, Noise = toxicNoise, Seamless = true };
		_material.SetShaderParameter("toxic_noise_texture", toxicNoiseTexture);

		var placeholder = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		placeholder.SetPixel(0, 0, Colors.White);
		_overlay = new Sprite2D
		{
			Texture = ImageTexture.CreateFromImage(placeholder),
			Centered = true,
			Material = _material,
			// Below LineOfSightSystem's darkness (ZIndex 4096) so gas is
			// still visibly darkened in unseen areas, but above everything
			// else — props, TileMap, lights.
			ZIndex = 4095,
			ZAsRelative = false,
		};
		AddChild(_overlay);
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}

	// Called by any hazard that wants to vent gas (see TimedHazardEmitter's
	// EmitsGas option, or GasVent) — deposits density of the given type into
	// whichever tile worldPosition falls in, at temperatureOverride (or
	// AmbientTemperature if omitted, e.g. a cold gas leak). The mixture's
	// TOTAL is only clamped to MaxPressure (a generous safety ceiling), NOT
	// to 1.0 — a sealed tile with nowhere to vent genuinely keeps
	// pressurizing past "visually full" instead of silently discarding
	// anything beyond it (see the Pressure export group). Temperature is
	// mass-weighted-blended into whatever was already there rather than
	// overwritten — a trickle of hot gas nudges a room's temperature
	// proportionally, it doesn't instantly reset it.
	public void EmitGas(Vector2 worldPosition, GasType type, float amount, float? temperatureOverride = null)
	{
		if (_tileMap == null) return;
		Vector2I tile = _tileMap.LocalToMap(_tileMap.ToLocal(worldPosition));

		_density.TryGetValue(tile, out GasMixture mix);
		float existingTotal = mix.Total;
		float incomingTemp = temperatureOverride ?? AmbientTemperature;

		mix[type] = mix[type] + amount;
		mix.Temperature = BlendTemperature(mix.Temperature, existingTotal, incomingTemp, amount);

		_density[tile] = mix.ClampedTo(MaxPressure);
	}

	// Read-path fallback for "what's actually in this tile," including
	// tiles _density has never tracked — the map's sparse dictionary only
	// ever holds cells something has actually emitted into (a deliberate,
	// load-bearing performance choice: cost scales with how much of the
	// level currently has gas on it, not the whole map), so this does NOT
	// write anything into _density or change that. A real tracked mixture
	// is returned as-is; an untouched tile instead gets a phantom baseline
	// of ordinary room air (AmbientOxygenLevel of Oxygen, AmbientTemperature)
	// so consumers that specifically care whether breathable air/ambient
	// heat is present (vent intake, combustion, room-heat checks) don't
	// have to special-case "nothing here yet" as a total vacuum. Not used
	// by the core simulation tick or rendering — see this system's plan
	// for why that's deliberately deferred to later systems.
	public GasMixture GetEffectiveMixture(Vector2I tile)
	{
		if (_density.TryGetValue(tile, out GasMixture mix)) return mix;
		return new GasMixture { Oxygen = AmbientOxygenLevel, Temperature = AmbientTemperature };
	}

	// A cell "matters" thermally/for cleanup purposes if it has real gas OR
	// its Temperature has actually drifted from ambient — used everywhere a
	// cell would otherwise be judged purely by Total (which is always 0 for
	// a heat-only tile a hazard has been warming with no gas involved).
	private bool HasThermalMass(GasMixture mix) => mix.Total > MinDensity || Mathf.Abs(mix.Temperature - AmbientTemperature) > MinHeatAboveAmbient;

	// Called by any hazard that heats a room without necessarily emitting
	// any gas into it (a fire, a heat lamp, etc.) — same shape as
	// EmitGas/ApplyScorch: small amounts called every frame, naturally
	// capped by HeatLossRate pulling everything back toward ambient once
	// the source stops, exactly the equilibrium pattern every other
	// continuous source in this project already relies on. Adds ZERO gas
	// of any type — purely a temperature nudge.
	public void AddHeat(Vector2 worldPosition, float degrees, float radius)
	{
		if (_tileMap == null) return;
		Vector2I center = WorldToTile(worldPosition);
		int tileRadius = Mathf.Max(0, Mathf.CeilToInt(radius / _tileSize.X));

		for (int dy = -tileRadius; dy <= tileRadius; dy++)
		{
			for (int dx = -tileRadius; dx <= tileRadius; dx++)
			{
				Vector2I tile = center + new Vector2I(dx, dy);
				if (IsSolid(tile)) continue;

				Vector2 tileWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
				float dist = tileWorld.DistanceTo(worldPosition);
				if (dist > radius) continue;

				float falloff = radius > 0f ? 1f - (dist / radius) : 1f;
				bool existed = _density.TryGetValue(tile, out GasMixture mix);
				float baseline = existed ? mix.Temperature : AmbientTemperature;
				mix.Temperature = baseline + degrees * falloff;
				_density[tile] = mix;
			}
		}
	}

	// Mass-weighted average — combining an existingAmount at existingTemp
	// with an incomingAmount at incomingTemp settles at the temperature
	// their relative masses pull it toward, not a flat average (a
	// thimbleful of hot gas shouldn't reset a whole room's temperature).
	private static float BlendTemperature(float existingTemp, float existingAmount, float incomingTemp, float incomingAmount)
	{
		float total = existingAmount + incomingAmount;
		return total > 0f ? (existingTemp * existingAmount + incomingTemp * incomingAmount) / total : existingTemp;
	}

	public override void _Process(double delta)
	{
		if (_tileMap == null) return;
		float dt = (float)delta;

		_tickTimer -= dt;
		if (_tickTimer <= 0f)
		{
			_tickTimer = TickInterval;
			SimulationTick();
		}

		Sam player = GetLocalPlayer();
		if (player == null) return;

		Camera2D camera = player.GetNodeOrNull<Camera2D>("PlayerCamera");
		if (camera == null || !camera.IsCurrent()) return;

		Vector2 cameraCenter = camera.GetScreenCenterPosition();
		Vector2 visibleWorldSize = (Vector2)GetViewport().GetVisibleRect().Size * camera.Zoom * 1.1f;
		Vector2I windowCenterTile = _tileMap.LocalToMap(_tileMap.ToLocal(cameraCenter));
		Vector2 gridCenterWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(windowCenterTile));

		_overlay.GlobalPosition = cameraCenter;
		_overlay.Scale = visibleWorldSize;
		_material.SetShaderParameter("sprite_world_size", visibleWorldSize);
		_material.SetShaderParameter("grid_offset_world", cameraCenter - gridCenterWorld);

		UpdateGasTexture(windowCenterTile);
		CheckPlayerExposure(player, dt);
		CheckPlayerHeatExposure(player, dt);

		// Always redrawn (cheap — Godot clears prior draw commands each
		// pass regardless), so flipping DebugVisualization off mid-play
		// actually clears the overlay next frame instead of leaving a
		// stale frozen snapshot on screen.
		_debugWindowCenterTile = windowCenterTile;
		QueueRedraw();
	}

	// Editor/Inspector-toggleable overlay (DebugVisualization) — draws the
	// Pipes layer's actual topology (plain connector tiles vs. real is_vent
	// intake/outtake points) and every nearby cell's exact per-type density,
	// so "is this tile actually a vent" or "why isn't this room building up"
	// can be answered by looking at the screen instead of guessing at
	// numbers or re-reading the simulation code.
	public override void _Draw()
	{
		if (!DebugVisualization || _tileMap == null) return;

		Font font = ThemeDB.FallbackFont;
		float tile = _tileSize.X;
		Vector2 half = new Vector2(tile, tile) * 0.5f;

		foreach (Vector2I pipeTile in _tileMap.GetUsedCells(_pipeLayerIndex))
		{
			Vector2 center = TileToLocal(pipeTile);
			bool isVent = IsVentTile(pipeTile);
			Color color = isVent ? DebugVentColor : DebugPipeColor;
			DrawRect(new Rect2(center - half, new Vector2(tile, tile)), color, false, isVent ? 3f : 1.5f);
			if (isVent)
			{
				DrawString(font, center + new Vector2(-half.X + 2f, -half.Y * 0.2f), "VENT", HorizontalAlignment.Left, -1, DebugFontSize, DebugVentColor);
			}
		}

		for (int dy = -WindowRadiusTiles; dy <= WindowRadiusTiles; dy++)
		{
			for (int dx = -WindowRadiusTiles; dx <= WindowRadiusTiles; dx++)
			{
				Vector2I tilePos = _debugWindowCenterTile + new Vector2I(dx, dy);
				if (!_density.TryGetValue(tilePos, out GasMixture mix) || mix.Total <= MinDensity) continue;

				Vector2 center = TileToLocal(tilePos);
				float lift = 1f - EffectiveWeight(mix);
				// Warmer/lighter reads orange, cooler/heavier reads blue —
				// a quick visual check that buoyancy is actually biasing
				// the right cells the right direction.
				Color tempColor = lift > 0f
					? new Color(1f, 0.6f + 0.4f * Mathf.Clamp(lift, 0f, 1f), 0.3f)
					: new Color(0.4f, 0.7f, 1f);
				// Pressure (mix.Total) is no longer capped at 1.0 — a cell
				// past "visually full" gets a red border that thickens
				// toward BurstPressure, so overpressure building up in a
				// sealed room is obvious even though the rendered gas alpha
				// itself always saturates at the same visual opacity.
				bool overPressure = mix.Total > 1f;
				Color rectColor = overPressure ? new Color(1f, 0.2f, 0.2f, 0.8f) : new Color(1f, 1f, 1f, 0.5f);
				float rectWidth = overPressure ? Mathf.Lerp(1.5f, 4f, Mathf.Clamp(mix.Total / BurstPressure, 0f, 1f)) : 1f;
				DrawRect(new Rect2(center - half, new Vector2(tile, tile)), rectColor, false, rectWidth);
				DrawString(font, center + new Vector2(-half.X + 2f, -2f), $"{mix.Total:0.00}", HorizontalAlignment.Left, -1, DebugFontSize, overPressure ? new Color(1f, 0.4f, 0.4f) : Colors.White);
				DrawString(font, center + new Vector2(-half.X + 2f, DebugFontSize + 2f), $"F{mix.Flammable:0.00} T{mix.Toxic:0.00} O{mix.Oxygen:0.00}", HorizontalAlignment.Left, -1, DebugFontSize - 1, Colors.White);
				DrawString(font, center + new Vector2(-half.X + 2f, DebugFontSize * 2 + 4f), $"{mix.Temperature:0.0}° lift{lift:+0.00;-0.00}", HorizontalAlignment.Left, -1, DebugFontSize - 1, tempColor);
			}
		}
	}

	private Vector2 TileToLocal(Vector2I tile)
	{
		return ToLocal(_tileMap.ToGlobal(_tileMap.MapToLocal(tile)));
	}

	private void UpdateGasTexture(Vector2I windowCenterTile)
	{
		for (int gy = 0; gy < _maskResolution; gy++)
		{
			int tileY = windowCenterTile.Y - WindowRadiusTiles + gy;
			for (int gx = 0; gx < _maskResolution; gx++)
			{
				int tileX = windowCenterTile.X - WindowRadiusTiles + gx;
				_density.TryGetValue(new Vector2I(tileX, tileY), out GasMixture mix);
				_maskImage.SetPixel(gx, gy, new Color(mix.Flammable, mix.Toxic, mix.Oxygen, Mathf.Min(mix.Total, 1f)));
			}
		}
		_maskTexture.Update(_maskImage);
	}

	// Only ever damages the LOCAL player — same "purely local concern" rule
	// LineOfSightSystem follows, since each peer simulates its own gas.
	// Damage scales with Pressure (mix.Total) past DamageThreshold — a
	// room that just barely crossed the threshold stings for DamagePerTick,
	// but a heavily pressurized sealed closet (Pressure well above
	// threshold, since gas is no longer capped at "visually full") hurts
	// proportionally more, not the same flat tick regardless of how bad it
	// actually is in there.
	private void CheckPlayerExposure(Sam player, float dt)
	{
		Vector2I tile = _tileMap.LocalToMap(_tileMap.ToLocal(player.GlobalPosition));
		_density.TryGetValue(tile, out GasMixture mix);

		if (mix.Total < DamageThreshold)
		{
			_damageTimer = 0f;
			return;
		}

		_damageTimer -= dt;
		if (_damageTimer <= 0f)
		{
			_damageTimer = DamageTickInterval;
			float overPressureRatio = mix.Total / DamageThreshold;
			int damage = Mathf.Max(DamagePerTick, Mathf.RoundToInt(DamagePerTick * overPressureRatio));
			player.GetNodeOrNull<HealthComponent>("HealthComponent")?.Damage(damage, Vector2.Zero);
		}
	}

	// Sibling to CheckPlayerExposure rather than merged into it — same tile
	// lookup, different concern (ambient room heat, not gas pressure). Uses
	// GetEffectiveMixture instead of a raw _density lookup so this correctly
	// reads heat-only cells (AddHeat) with zero gas mass, not just gas-filled
	// ones.
	private void CheckPlayerHeatExposure(Sam player, float dt)
	{
		Vector2I tile = _tileMap.LocalToMap(_tileMap.ToLocal(player.GlobalPosition));
		GasMixture mix = GetEffectiveMixture(tile);

		if (mix.Temperature < RoomHeatDamageThreshold)
		{
			_heatDamageTimer = 0f;
		}
		else
		{
			_heatDamageTimer -= dt;
			if (_heatDamageTimer <= 0f)
			{
				_heatDamageTimer = RoomHeatDamageTickInterval;
				float overHeatRatio = mix.Temperature / RoomHeatDamageThreshold;
				int damage = Mathf.Max(RoomHeatDamagePerTick, Mathf.RoundToInt(RoomHeatDamagePerTick * overHeatRatio));
				player.GetNodeOrNull<HealthComponent>("HealthComponent")?.Damage(damage, Vector2.Zero);
			}
		}

		if (mix.Temperature < BurningIgnitionTemperature)
		{
			_burningCheckTimer = 0f;
			return;
		}

		_burningCheckTimer -= dt;
		if (_burningCheckTimer <= 0f)
		{
			_burningCheckTimer = BurningCheckInterval;
			player.ApplyBurning(BurningDuration);
		}
	}

	private Sam GetLocalPlayer()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is Sam sam && (!sam.IsNetworked || sam.IsMultiplayerAuthority()))
				return sam;
		}
		return null;
	}

	// A real physics shape query against whatever actually collides on
	// SolidCollisionMask, not a specific TileMap layer index — a "Solid
	// layer number" export silently goes stale the moment a level's layers
	// get reordered or renamed (a wall tile painted on the wrong-numbered
	// layer, or a new layer inserted shifting every index after it), while
	// this always matches whatever the player/turrets physically collide
	// with regardless of which visual layer painted it. The ONLY way gas
	// crosses between two sealed rooms is through a dedicated vent tile
	// (is_vent, on the Pipes layer) via ProcessPipeNetworks' pooled
	// suction/redistribution — a plain wall never lets ordinary diffusion
	// leak through it, full stop.
	//
	// Cleared once per SimulationTick (not per query) — solidity doesn't
	// change mid-tick, and the same handful of wall tiles get re-checked
	// many times over as different cells probe their shared neighbors.
	private readonly Dictionary<Vector2I, bool> _solidCache = new();
	private RectangleShape2D _solidProbeShape;

	private bool IsSolid(Vector2I tile)
	{
		if (_solidCache.TryGetValue(tile, out bool cached)) return cached;

		_solidProbeShape ??= new RectangleShape2D { Size = (Vector2)_tileSize * 0.9f };
		Vector2 worldCenter = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
		var query = new PhysicsShapeQueryParameters2D
		{
			Shape = _solidProbeShape,
			Transform = new Transform2D(0f, worldCenter),
			CollisionMask = SolidCollisionMask,
			CollideWithBodies = true,
			CollideWithAreas = false,
		};

		bool solid = GetWorld2D().DirectSpaceState.IntersectShape(query, 1).Count > 0;
		_solidCache[tile] = solid;
		return solid;
	}

	private static readonly Vector2I[] NeighborOffsets =
	{
		new Vector2I(1, 0), new Vector2I(-1, 0), new Vector2I(0, 1), new Vector2I(0, -1)
	};

	private void SimulationTick()
	{
		_solidCache.Clear();
		if (_density.Count > 0) SimulationTickFluid();
		ProcessPipeNetworks();
		CheckPressureBursts();
		CheckIgnition();
	}

	// Fires once per cell per over-threshold excursion (not every tick it
	// stays over) — GasSimulation doesn't know or care what listens; a
	// weak door, a vent cap, a wall panel can all subscribe to PressureBurst
	// and react however makes sense for that hazard.
	private readonly HashSet<Vector2I> _burstNotified = new();

	private void CheckPressureBursts()
	{
		foreach (KeyValuePair<Vector2I, GasMixture> pair in _density)
		{
			bool over = pair.Value.Total >= BurstPressure;
			if (over)
			{
				if (_burstNotified.Add(pair.Key))
				{
					Vector2 worldPosition = _tileMap.ToGlobal(_tileMap.MapToLocal(pair.Key));
					EmitSignal(SignalName.PressureBurst, pair.Key, worldPosition, pair.Value.Total);
				}
			}
			else
			{
				_burstNotified.Remove(pair.Key);
			}
		}
	}

	// Passive autoignition — no external spark needed, a flammable cell
	// just gets hot enough on its own (drifted near a hot vent, or caught
	// the heat spike from a NEIGHBOR igniting last tick). This second case
	// is what makes fire actually propagate through a connected flammable
	// cloud: Ignite() spikes the igniting cell's Temperature, that spreads
	// to neighbors via the existing HeatConductionRate blending, and any
	// neighbor still flammable and now hot enough ignites here on its own
	// following tick — a chain reaction with no separate spread algorithm.
	//
	// Collected into a list first rather than igniting while iterating —
	// IgniteChain mutates _density, which would invalidate the enumerator.
	private List<Vector2I> _ignitionScratch;

	private void CheckIgnition()
	{
		_ignitionScratch?.Clear();
		foreach (KeyValuePair<Vector2I, GasMixture> pair in _density)
		{
			if (pair.Value.Flammable < IgnitionMinFlammable) continue;
			if (pair.Value.Temperature < IgnitionTemperature) continue;
			(_ignitionScratch ??= new List<Vector2I>()).Add(pair.Key);
		}

		if (_ignitionScratch == null) return;
		foreach (Vector2I tile in _ignitionScratch)
		{
			IgniteChain(tile);
		}
	}

	// Called by any external fire/spark source (a laser beam, a bolt, a
	// spark hazard) to try to ignite flammable gas at/near worldPosition
	// RIGHT NOW, regardless of its current temperature — an actual flame
	// touching a flammable cloud ignites it on contact, it doesn't need to
	// slow-cook the cell up to IgnitionTemperature first the way passive
	// autoignition does. Returns whether anything actually caught.
	public bool TryIgnite(Vector2 worldPosition, float radius = -1f)
	{
		if (_tileMap == null) return false;
		float searchRadius = radius >= 0f ? radius : IgnitionRadius;
		Vector2I center = _tileMap.LocalToMap(_tileMap.ToLocal(worldPosition));
		int tileRadius = Mathf.Max(1, Mathf.CeilToInt(searchRadius / _tileSize.X));

		bool ignitedAny = false;
		for (int dy = -tileRadius; dy <= tileRadius; dy++)
		{
			for (int dx = -tileRadius; dx <= tileRadius; dx++)
			{
				Vector2I tile = center + new Vector2I(dx, dy);
				if (!_density.TryGetValue(tile, out GasMixture mix) || mix.Flammable < IgnitionMinFlammable) continue;
				if (IsSolid(tile)) continue; // fire can't reach through a wall

				Vector2 tileWorld = _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
				if (tileWorld.DistanceTo(worldPosition) > searchRadius) continue;

				IgniteChain(tile);
				ignitedAny = true;
			}
		}
		return ignitedAny;
	}

	// Flood-fills outward from originTile through connected, still-
	// flammable, non-solid tiles (bounded by MaxChainReactionTiles) to find
	// the WHOLE pocket about to go up together, ignites every tile in it,
	// then detonates ONE explosion scaled to how much gas the whole pocket
	// actually had — a big room going up together reads as one
	// proportionally bigger blast, not a stutter of overlapping small ones.
	// Safe to call on a tile that's part of (or already consumed by) an
	// earlier chain this same tick/frame — it just no-ops if origin isn't
	// (or is no longer) flammable enough.
	private readonly HashSet<Vector2I> _chainVisited = new();
	private readonly Queue<Vector2I> _chainQueue = new();
	private readonly List<Vector2I> _chainTiles = new();

	private void IgniteChain(Vector2I originTile)
	{
		// Starting a fire still needs a real concentration — only the
		// origin is held to IgnitionMinFlammable. Everything the flood
		// fill reaches FROM there is judged against the much lower
		// ChainPropagationMinFlammable instead (see its own comment).
		if (!_density.TryGetValue(originTile, out GasMixture originMix) || originMix.Flammable < IgnitionMinFlammable) return;

		_chainVisited.Clear();
		_chainQueue.Clear();
		_chainTiles.Clear();
		_chainQueue.Enqueue(originTile);
		_chainVisited.Add(originTile);

		float totalFlammable = 0f;
		while (_chainQueue.Count > 0 && _chainTiles.Count < MaxChainReactionTiles)
		{
			Vector2I current = _chainQueue.Dequeue();
			if (!_density.TryGetValue(current, out GasMixture mix) || mix.Flammable < ChainPropagationMinFlammable) continue;

			_chainTiles.Add(current);
			totalFlammable += mix.Flammable;

			foreach (Vector2I offset in NeighborOffsets)
			{
				Vector2I neighbor = current + offset;
				if (_chainVisited.Contains(neighbor) || IsSolid(neighbor)) continue;
				if (!_density.TryGetValue(neighbor, out GasMixture neighborMix) || neighborMix.Flammable < ChainPropagationMinFlammable) continue;

				_chainVisited.Add(neighbor);
				_chainQueue.Enqueue(neighbor);
			}
		}

		if (_chainTiles.Count == 0) return;

		Vector2 centroidWorld = Vector2.Zero;
		foreach (Vector2I tile in _chainTiles)
		{
			centroidWorld += _tileMap.ToGlobal(_tileMap.MapToLocal(tile));
		}
		centroidWorld /= _chainTiles.Count;

		foreach (Vector2I tile in _chainTiles)
		{
			if (_density.TryGetValue(tile, out GasMixture tileMix)) Ignite(tile, tileMix);
		}

		ExplosionTier tier = totalFlammable >= LargeExplosionFlammable ? ExplosionTier.Large
			: totalFlammable >= MediumExplosionFlammable ? ExplosionTier.Medium
			: ExplosionTier.Small;
		DetonateExplosion(centroidWorld, tier, totalFlammable);
	}

	// Pure physical reaction, one tile — burns away the Flammable content
	// (consumed by the reaction; Toxic/Oxygen mixed into the same cell are
	// untouched) and dumps IgnitionHeatRelease into the cell's own
	// Temperature so the fire can spread further via ordinary heat
	// conduction. Deliberately does NOT do damage/VFX/SFX itself — that's
	// DetonateExplosion's job, once per chain reaction, not once per tile.
	private void Ignite(Vector2I tile, GasMixture mix)
	{
		GasMixture burned = mix;
		burned.Flammable = 0f;
		burned.Temperature += IgnitionHeatRelease;
		_density[tile] = burned.ClampedTo(MaxPressure);
	}

	// Only ever picks the VFX Scale multiplier now — radius/damage are
	// continuous functions of totalFlammableConsumed (see DetonateExplosion),
	// not a per-tier lookup.
	private float TierScale(ExplosionTier tier) => tier switch
	{
		ExplosionTier.Large => LargeExplosionScaleMultiplier,
		ExplosionTier.Medium => MediumExplosionScaleMultiplier,
		_ => 1f,
	};

	// The actual "explosion" as the player experiences it, one call per
	// chain reaction regardless of how many individual tiles IgniteChain
	// just burned through: knockback+damage the player, shove nearby
	// rigidbodies, play the tiered sprite burst, spawn a shockwave ring,
	// scorch and structurally damage tiles, break nearby vents, then
	// signal. Every radius/amount below derives from the same blastRadius/
	// totalFlammableConsumed/scaleMul, so a bigger gas pocket consistently
	// produces a bigger version of ALL of these together.
	private void DetonateExplosion(Vector2 worldPosition, ExplosionTier tier, float totalFlammableConsumed)
	{
		float blastRadius = IgnitionRadius + ExplosionRadiusPerFlammableUnit * totalFlammableConsumed;
		float blastDamage = IgnitionDamage + ExplosionDamagePerFlammableUnit * totalFlammableConsumed;
		float scaleMul = TierScale(tier);

		DamageAndKnockbackNearbyPlayer(worldPosition, blastRadius, blastDamage);
		ApplyShockwaveToProps(worldPosition, blastRadius, ExplosionPropImpulseStrength);
		SpawnIgnitionBurst(worldPosition, tier, scaleMul);
		SpawnShockwaveRing(worldPosition, blastRadius);
		EnvironmentDamage.Instance?.ApplyScorch(worldPosition, ExplosionScorchAmount * scaleMul, blastRadius);
		EnvironmentDamage.Instance?.ApplyStructuralDamage(worldPosition, ExplosionStructuralDamage * scaleMul, blastRadius);
		EnvironmentDamage.Instance?.SpawnDebris(worldPosition, scaleMul, new Color(0.5f, 0.32f, 0.15f, 1f));
		BreakNearbyVents(worldPosition, blastRadius);
		EmitSignal(SignalName.GasIgnited, worldPosition, (int)tier, totalFlammableConsumed);
	}

	// Only ever the LOCAL player, same "purely local concern" rule every
	// other player-facing check in this file follows. Knockback direction
	// is deliberately NOT normalized to unit length — its length IS the
	// push strength, exploiting Sam.OnTookDamage's existing
	// `Velocity = knockbackDirection * 100f` pipeline unchanged so this
	// stays entirely inside GasSimulation.cs.
	private void DamageAndKnockbackNearbyPlayer(Vector2 worldPosition, float radius, float damageAmount)
	{
		Sam player = GetLocalPlayer();
		if (player == null) return;

		float dist = player.GlobalPosition.DistanceTo(worldPosition);
		if (dist > radius) return;

		float falloff = radius > 0f ? 1f - (dist / radius) : 1f;
		Vector2 direction = player.GlobalPosition == worldPosition
			? Vector2.Up
			: (player.GlobalPosition - worldPosition).Normalized();
		Vector2 knockback = direction * (ExplosionKnockbackStrength * falloff);

		int damage = Mathf.Max(1, Mathf.RoundToInt(damageAmount));
		player.GetNodeOrNull<HealthComponent>("HealthComponent")?.Damage(damage, knockback);
	}

	// Stock Godot physics — no bespoke prop-side code needed. Every
	// RigidBody2D caught in the query (crates, etc.) gets shoved away from
	// the blast center, falling off toward the edge of radius.
	private void ApplyShockwaveToProps(Vector2 worldPosition, float radius, float impulseStrength)
	{
		var query = new PhysicsShapeQueryParameters2D
		{
			Shape = new CircleShape2D { Radius = radius },
			Transform = new Transform2D(0f, worldPosition),
			CollisionMask = SolidCollisionMask,
			CollideWithBodies = true,
			CollideWithAreas = false,
		};

		Godot.Collections.Array<Godot.Collections.Dictionary> results = GetWorld2D().DirectSpaceState.IntersectShape(query, 32);
		foreach (Godot.Collections.Dictionary result in results)
		{
			if (result["collider"].AsGodotObject() is not RigidBody2D body) continue;

			float dist = body.GlobalPosition.DistanceTo(worldPosition);
			float falloff = radius > 0f ? 1f - Mathf.Clamp(dist / radius, 0f, 1f) : 1f;
			Vector2 direction = body.GlobalPosition == worldPosition ? Vector2.Up : (body.GlobalPosition - worldPosition).Normalized();
			body.ApplyCentralImpulse(direction * (impulseStrength * falloff));
		}
	}

	// Code-built one-shot ring — a radial GradientTexture2D (same bright-
	// center/transparent-edge shape as the vent glow templates elsewhere in
	// this project) scaled from near-zero up to the blast radius and faded
	// out, same "code-built, parent to current scene, self-cleanup"
	// convention as SpawnIgnitionBurst.
	private void SpawnShockwaveRing(Vector2 worldPosition, float radius)
	{
		Node parent = GetTree().CurrentScene;
		if (parent == null) return;

		var ringGradient = new Gradient();
		ringGradient.SetColor(0, new Color(1f, 0.85f, 0.6f, 0.9f));
		ringGradient.SetColor(1, new Color(1f, 0.6f, 0.3f, 0f));

		var ringTexture = new GradientTexture2D
		{
			Gradient = ringGradient,
			Width = 128,
			Height = 128,
			Fill = GradientTexture2D.FillEnum.Radial,
			FillFrom = new Vector2(0.5f, 0.5f),
			FillTo = new Vector2(1f, 0.5f),
		};

		var ring = new Sprite2D
		{
			Texture = ringTexture,
			GlobalPosition = worldPosition,
			Scale = Vector2.One * 0.05f,
			Modulate = new Color(1f, 1f, 1f, 0.9f),
			ZIndex = 20,
		};
		parent.AddChild(ring);

		float targetScale = radius / (ringTexture.Width * 0.5f);
		Tween tween = ring.CreateTween();
		tween.SetParallel(true);
		tween.TweenProperty(ring, "scale", Vector2.One * targetScale, 0.35f).SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(ring, "modulate:a", 0f, 0.35f).SetTrans(Tween.TransitionType.Linear);
		tween.Chain().TweenCallback(Callable.From(() => { if (IsInstanceValid(ring)) ring.QueueFree(); }));
	}

	// Vents/GasVents caught in the blast switch to their broken sprite and
	// permanently stop functioning (see GasVent.Break()/
	// TimedHazardEmitter.Break(), System 2) — only instances that opted in
	// via DestructibleByExplosion register into the "ExplodableVent" group
	// in the first place, so this never touches unrelated hazards.
	private void BreakNearbyVents(Vector2 worldPosition, float radius)
	{
		foreach (Node node in GetTree().GetNodesInGroup("ExplodableVent"))
		{
			if (node is not Node2D node2D) continue;
			if (node2D.GlobalPosition.DistanceTo(worldPosition) > radius) continue;

			if (node is GasVent gasVent) gasVent.Break();
			else if (node is TimedHazardEmitter hazardEmitter) hazardEmitter.Break();
		}
	}

	// Tiered AnimatedSprite2D burst (GasExplosion.tscn — real sprite frames,
	// see GasExplosionEffect.cs) + a disposable AudioStreamPlayer2D, parented
	// to the current scene (not this node or the gas tile) so both survive
	// even if the igniting cell's gas is gone by the next frame. The sprite
	// self-frees on AnimationFinished; the audio player frees itself below.
	private void SpawnIgnitionBurst(Vector2 worldPosition, ExplosionTier tier, float scale)
	{
		Node parent = GetTree().CurrentScene;
		if (parent == null) return;

		var explosion = GD.Load<PackedScene>("res://Scenes/FX/GasExplosion.tscn").Instantiate<GasExplosionEffect>();
		explosion.Tier = tier;
		explosion.GlobalPosition = worldPosition;
		explosion.Scale = new Vector2(scale, scale);
		parent.AddChild(explosion);

		var audio = new AudioStreamPlayer2D
		{
			GlobalPosition = worldPosition,
			Bus = "SFX",
			MaxDistance = 900f * scale,
			VolumeDb = -4f,
			Stream = GD.Load<AudioStream>(IgnitionSounds[_rng.RandiRange(0, IgnitionSounds.Length - 1)]),
		};
		parent.AddChild(audio);
		audio.Play();
		audio.Finished += () => { if (IsInstanceValid(audio)) audio.QueueFree(); };
	}

	// A gas parcel's tendency to rise or sink, independent of concentration
	// — below 1.0 is lighter than ambient air (rises), above 1.0 is heavier
	// (sinks). Combines each type's baseline weight (composition-weighted,
	// so a Flammable/Toxic mix sits between the two) with a heat term: hot
	// gas expands and becomes effectively lighter, cold gas contracts and
	// becomes effectively heavier — the same reason a hot air balloon
	// rises and cold air pools at the floor.
	private float EffectiveWeight(GasMixture mix)
	{
		float total = mix.Total;
		float baseWeight = total > 0f
			? (mix.Flammable * FlammableWeight + mix.Toxic * ToxicWeight + mix.Oxygen * OxygenWeight) / total
			: OxygenWeight;
		return baseWeight - (mix.Temperature - AmbientTemperature) * HeatBuoyancyPerDegree;
	}

	private static readonly Vector2I UpOffset = new Vector2I(0, -1);
	private static readonly Vector2I DownOffset = new Vector2I(0, 1);

	// Two-stage flow, closer to how an actual liquid settles than pure
	// concentration diffusion:
	//
	// 1. SETTLE — heavy gas rushes straight down, light gas straight up,
	//    toward its preferred vertical neighbor, capped only by how much
	//    ROOM that neighbor has left (1.0 - its Total), not by the
	//    concentration difference. Gravity doesn't care whether the floor
	//    already has some gas sitting on it, only whether there's still
	//    space — this is what actually produces a flat-topped pool instead
	//    of an even diffuse haze.
	// 2. LEVEL — whatever's left over (either the gas has no strong
	//    buoyancy, or its preferred direction is already full/solid) spreads
	//    via ordinary gradient diffusion into every remaining open neighbor
	//    (never the direction SETTLE already claimed, to avoid double-
	//    counting the same neighbor twice in one tick). This is what makes
	//    a pool that's hit the floor spread out sideways to find its own
	//    level, and is also the ONLY movement a neutrally-buoyant gas
	//    (near-ambient Oxygen) ever gets, so it still fills a whole room
	//    (including vertically) exactly as before.
	private void SimulationTickFluid()
	{
		var next = new Dictionary<Vector2I, GasMixture>();
		var heatWeightSum = new Dictionary<Vector2I, float>();
		// Tracks the WEIGHT actually used per cell — normally identical to
		// next[key].Total, except for a heat-only cell (see AddHeat) where a
		// nominal floor is used instead of its true (zero) mass so its
		// temperature survives FinalizeTemperatures' weighted average
		// instead of dividing by zero and collapsing to AmbientTemperature.
		var heatMassSum = new Dictionary<Vector2I, float>();
		var flows = new List<(Vector2I neighbor, float weight)>(4);

		foreach (KeyValuePair<Vector2I, GasMixture> pair in _density)
		{
			GasMixture mix = pair.Value;
			float total = mix.Total;
			// Still carried forward into next tick even below MinDensity —
			// only ApplyDecay's final cleanup pass actually culls
			// negligible cells. Previously this just vanished the cell
			// outright without moving on, which read as gas disappearing
			// far faster than DecayRate alone would ever explain.
			if (total <= MinDensity)
			{
				Accumulate(next, pair.Key, mix);
				// Mathf.Max(..., HeatOnlyNominalMass): a heat-only cell (see
				// AddHeat) has zero real mass, so weighting its own carried-
				// forward temperature by mix.Total alone would contribute
				// nothing to FinalizeTemperatures' weighted average — and
				// with total weight 0, that average resolves to
				// AmbientTemperature, silently erasing the heat every tick.
				// Real gas-bearing cells never hit this floor (their true
				// mass already exceeds it).
				AccumulateHeat(heatWeightSum, heatMassSum, pair.Key, Mathf.Max(mix.Total, HeatOnlyNominalMass), mix.Temperature);
				continue;
			}

			// Positive lift = lighter than ambient air (wants to rise).
			float lift = 1f - EffectiveWeight(mix);
			float remaining = total;
			Vector2I? claimedOffset = null;

			if (Mathf.Abs(lift) > 0.01f)
			{
				Vector2I vertOffset = lift < 0f ? DownOffset : UpOffset;
				Vector2I vertNeighbor = pair.Key + vertOffset;
				if (!IsSolid(vertNeighbor))
				{
					claimedOffset = vertOffset;
					_density.TryGetValue(vertNeighbor, out GasMixture vertMix);
					float capacity = Mathf.Max(0f, 1f - vertMix.Total);
					float settleAmount = Mathf.Min(total * SettleRate, capacity);
					if (settleAmount > 0f)
					{
						GasMixture settled = mix * (settleAmount / total);
						Accumulate(next, vertNeighbor, settled);
						AccumulateHeat(heatWeightSum, heatMassSum, vertNeighbor, settled.Total, mix.Temperature);
						remaining -= settleAmount;
					}
				}
			}

			flows.Clear();
			float totalGradient = 0f;
			foreach (Vector2I offset in NeighborOffsets)
			{
				if (offset == claimedOffset) continue;

				Vector2I neighbor = pair.Key + offset;
				if (IsSolid(neighbor)) continue;

				_density.TryGetValue(neighbor, out GasMixture neighborMix);
				float gradient = total - neighborMix.Total;
				if (gradient <= 0f) continue;

				flows.Add((neighbor, gradient));
				totalGradient += gradient;
			}

			if (totalGradient <= 0f)
			{
				GasMixture selfRemainder = mix * (remaining / total);
				Accumulate(next, pair.Key, selfRemainder);
				AccumulateHeat(heatWeightSum, heatMassSum, pair.Key, selfRemainder.Total, mix.Temperature);
				continue;
			}

			// Outflow scales with how UNEVEN the neighborhood actually is,
			// not a flat fraction of self — previously this pushed away a
			// flat 50% of a cell's gas every tick whenever ANY neighbor was
			// even slightly lower, which in an open room (true almost
			// everywhere) detonated a cloud outward across dozens of tiles
			// in under a second, diluting every tile below the visible
			// threshold near-instantly. Now a cell surrounded by roughly
			// equal density barely moves, while a sharp gradient (fresh
			// source next to empty space) still flows fast and naturally
			// slows as the neighborhood equalizes.
			//
			// FlowRate is divided across the neighbor count (not applied to
			// the summed gradient directly) — a cell open on all 4 sides has
			// up to 4x the "total gradient" of a cell open on only one side,
			// and without this normalization that let FlowRate*totalGradient
			// blow straight past `total`, fully draining the cell in a
			// single tick. The next tick then does the same in reverse once
			// the neighbors are suddenly full, producing a flickering
			// full-drain/full-refill oscillation between adjacent tiles
			// (the "chessboard" pattern) instead of a smooth, settling fill.
			// Bounding it to at most FlowRate's own share of `total`
			// regardless of how many sides are open keeps every step a
			// partial, converging step toward equilibrium.
			float perFaceRate = FlowRate / NeighborOffsets.Length;
			float outflowAmount = Mathf.Min(totalGradient * perFaceRate, remaining);
			GasMixture totalOutflow = mix * (outflowAmount / total);
			GasMixture selfRemaining = mix * (remaining / total) - totalOutflow;
			Accumulate(next, pair.Key, selfRemaining);
			AccumulateHeat(heatWeightSum, heatMassSum, pair.Key, selfRemaining.Total, mix.Temperature);

			foreach ((Vector2I neighbor, float weight) in flows)
			{
				GasMixture portion = totalOutflow * (weight / totalGradient);
				Accumulate(next, neighbor, portion);
				AccumulateHeat(heatWeightSum, heatMassSum, neighbor, portion.Total, mix.Temperature);
			}
		}

		FinalizeTemperatures(next, heatWeightSum, heatMassSum);
		ApplyHeatConduction(next);
		ApplyDecay(next);
	}

	// heatMassSum tracks the WEIGHT actually used per contribution —
	// normally identical to amountTotal, except a heat-only cell's own
	// carried-forward contribution uses a nominal floor instead of its true
	// (zero) mass (see SimulationTickFluid's carry-forward branch), so this
	// has to be threaded through separately rather than re-derived from
	// next[key].Total afterward.
	private static void AccumulateHeat(Dictionary<Vector2I, float> heatWeightSum, Dictionary<Vector2I, float> heatMassSum, Vector2I key, float amountTotal, float temperature)
	{
		heatWeightSum.TryGetValue(key, out float existingHeat);
		heatWeightSum[key] = existingHeat + amountTotal * temperature;
		heatMassSum.TryGetValue(key, out float existingMass);
		heatMassSum[key] = existingMass + amountTotal;
	}

	// The amount-only +/-/* operators leave Temperature meaningless on
	// anything built out of them (mass bookkeeping only) — this resolves
	// every cell's real, weighted temperature from the heat carried by each
	// contribution that landed there this tick, exactly once per cell after
	// all mass movement for the tick is done. Divides by heatMassSum (the
	// weight actually used per contribution), NOT next[key].Total — for a
	// heat-only cell those differ on purpose (see AccumulateHeat).
	private void FinalizeTemperatures(Dictionary<Vector2I, GasMixture> next, Dictionary<Vector2I, float> heatWeightSum, Dictionary<Vector2I, float> heatMassSum)
	{
		foreach (Vector2I key in new List<Vector2I>(next.Keys))
		{
			GasMixture mix = next[key];
			float weighted = heatWeightSum.TryGetValue(key, out float w) ? w : 0f;
			float mass = heatMassSum.TryGetValue(key, out float m) ? m : 0f;
			mix.Temperature = mass > 0f ? weighted / mass : AmbientTemperature;
			next[key] = mix;
		}
	}

	// Plain thermal conduction between neighboring gas cells, independent
	// of any mass flow — a still, sealed pocket of hot gas next to a cold
	// one equalizes over time even if neither is moving anywhere. Blends
	// toward the neighbors' AVERAGE (not a summed gradient) specifically so
	// this can't reproduce the same overshoot/oscillation the mass-flow
	// model had before it was normalized by neighbor count — an average is
	// naturally bounded regardless of how many neighbors exist.
	private void ApplyHeatConduction(Dictionary<Vector2I, GasMixture> next)
	{
		var updates = new Dictionary<Vector2I, float>();
		foreach (KeyValuePair<Vector2I, GasMixture> pair in next)
		{
			if (!HasThermalMass(pair.Value)) continue;

			float sum = 0f;
			int count = 0;
			foreach (Vector2I offset in NeighborOffsets)
			{
				Vector2I neighbor = pair.Key + offset;
				if (IsSolid(neighbor)) continue;
				if (next.TryGetValue(neighbor, out GasMixture neighborMix) && HasThermalMass(neighborMix))
				{
					sum += neighborMix.Temperature;
					count++;
				}
			}

			if (count == 0) continue;
			updates[pair.Key] = Mathf.Lerp(pair.Value.Temperature, sum / count, HeatConductionRate);
		}

		foreach (KeyValuePair<Vector2I, float> update in updates)
		{
			GasMixture mix = next[update.Key];
			mix.Temperature = update.Value;
			next[update.Key] = mix;
		}
	}

	private void ApplyDecay(Dictionary<Vector2I, GasMixture> next)
	{
		var final = new Dictionary<Vector2I, GasMixture>();
		foreach (KeyValuePair<Vector2I, GasMixture> pair in next)
		{
			GasMixture decayed = pair.Value * (1f - DecayRate);
			decayed.Temperature = Mathf.Lerp(pair.Value.Temperature, AmbientTemperature, HeatLossRate);
			// HasThermalMass (not a raw Total check) — a heat-only cell (see
			// AddHeat) survives here until it's actually cooled back to near
			// ambient, instead of being pruned the instant it's checked
			// purely because it never had any gas mass to begin with.
			if (HasThermalMass(decayed))
			{
				final[pair.Key] = decayed.ClampedTo(MaxPressure);
			}
		}
		_density = final;
	}

	private static GasMixture Accumulate(Dictionary<Vector2I, GasMixture> dict, Vector2I key, GasMixture amount)
	{
		dict.TryGetValue(key, out GasMixture existing);
		GasMixture updated = existing + amount;
		dict[key] = updated;
		return updated;
	}

	// Scans the TileMap's actual layers for one named PipeLayerName instead
	// of trusting the raw PipeLayer index — falls back to PipeLayer only if
	// no layer with that name exists (e.g. PipeLayerName left blank).
	private int ResolvePipeLayer()
	{
		int count = _tileMap.GetLayersCount();
		for (int i = 0; i < count; i++)
		{
			if (_tileMap.GetLayerName(i) == PipeLayerName) return i;
		}

		if (!string.IsNullOrEmpty(PipeLayerName))
		{
			GD.PushWarning($"GasSimulation: no TileMap layer named '{PipeLayerName}' found — falling back to PipeLayer index {PipeLayer}. Piping will silently do nothing if that index isn't actually the Pipes layer.");
		}
		return PipeLayer;
	}

	// Flood-fills the Pipes layer into connected runs, recording the vent
	// tiles (is_vent = true) in each plus the run's total cell count (used
	// as a physical-length proxy for travel time) — plain connector pipe
	// tiles establish which vents belong to the same network and set that
	// length, but otherwise carry no gas of their own (no gas is tracked
	// "inside" a connector tile; only vent tiles ever hold density, and
	// what's between them exists purely as in-transit parcels — see
	// ProcessPipeNetworks).
	private void BuildPipeNetworks()
	{
		_pipeNetworks.Clear();
		_pipeNetworkLength.Clear();
		_pipeNetworkTransit.Clear();
		_pipeNetworkLeakCells.Clear();

		var pipeCells = new HashSet<Vector2I>(_tileMap.GetUsedCells(_pipeLayerIndex));
		if (pipeCells.Count == 0) return;

		var visited = new HashSet<Vector2I>();
		foreach (Vector2I start in pipeCells)
		{
			if (visited.Contains(start)) continue;

			var vents = new List<Vector2I>();
			var leakCells = new List<Vector2I>();
			var queue = new Queue<Vector2I>();
			queue.Enqueue(start);
			visited.Add(start);
			int length = 0;

			while (queue.Count > 0)
			{
				Vector2I current = queue.Dequeue();
				length++;
				if (IsVentTile(current)) vents.Add(current);
				if (CanLeak(current)) leakCells.Add(current);

				foreach (Vector2I offset in NeighborOffsets)
				{
					Vector2I next = current + offset;
					if (pipeCells.Contains(next) && visited.Add(next))
					{
						queue.Enqueue(next);
					}
				}
			}

			if (vents.Count > 0)
			{
				_pipeNetworks.Add(vents);
				_pipeNetworkLength.Add(length);
				_pipeNetworkTransit.Add(new Queue<(GasMixture, int)>());
				_pipeNetworkLeakCells.Add(leakCells);
			}
		}
	}

	private bool CanLeak(Vector2I tile)
	{
		TileData tileData = _tileMap.GetCellTileData(_pipeLayerIndex, tile);
		return tileData != null && tileData.GetCustomData(CanLeakDataName).AsBool();
	}

	private bool IsVentTile(Vector2I tile)
	{
		TileData tileData = _tileMap.GetCellTileData(_pipeLayerIndex, tile);
		return tileData != null && tileData.GetCustomData(IsVentDataName).AsBool();
	}

	// Every open tile within VentPullRadiusTiles of a vent, paired with a
	// distance-based weight (closer tiles pull more) — this is what makes a
	// vent read as active suction on the whole nearby room instead of only
	// ever reacting to gas that happens to reach the one tile it's flush
	// against.
	private List<(Vector2I cell, float weight)> GetVentPullCells(Vector2I ventTile)
	{
		var result = new List<(Vector2I, float)>();
		int r = VentPullRadiusTiles;
		for (int dy = -r; dy <= r; dy++)
		{
			for (int dx = -r; dx <= r; dx++)
			{
				if (dx == 0 && dy == 0) continue;
				float dist = Mathf.Sqrt(dx * dx + dy * dy);
				if (dist > r) continue;

				Vector2I cell = ventTile + new Vector2I(dx, dy);
				if (IsSolid(cell)) continue;

				result.Add((cell, 1f / (1f + dist)));
			}
		}
		return result;
	}

	// Real travel time, not instant same-tick teleportation: each network
	// tick, every live (non-broken) Intake vent pulls VentFlowRate's share
	// of the gas within its pull radius (weighted by distance, preserving
	// composition) into ONE new parcel, which then takes
	// networkLength/PipeTravelTilesPerTick ticks in transit before
	// releasing into every live Output vent's own pull radius — a run
	// venting a hot room and a cold room genuinely arrives mixed and
	// lukewarm at the outputs, not an instantaneous single-tick swap.
	// A vent tile nothing has registered a mode for defaults to Output
	// (matching the old unconditional pull-and-push-evenly behavior for
	// any is_vent tile that doesn't have a scene-placed prop on it yet).
	private void ProcessPipeNetworks()
	{
		if (_pipeNetworks.Count == 0) return;

		for (int n = 0; n < _pipeNetworks.Count; n++)
		{
			List<Vector2I> network = _pipeNetworks[n];
			var intakes = new List<Vector2I>();
			var outputs = new List<Vector2I>();
			foreach (Vector2I vent in network)
			{
				if (_ventBroken.TryGetValue(vent, out bool broken) && broken) continue;
				VentMode mode = _ventModes.TryGetValue(vent, out VentMode m) ? m : VentMode.Output;
				(mode == VentMode.Intake ? intakes : outputs).Add(vent);
			}

			// 1. Intakes pull from their local rooms into a fresh parcel —
			// only if there's actually somewhere for it to go, otherwise
			// gas would vanish into a dead-end network forever.
			if (outputs.Count > 0 && intakes.Count > 0)
			{
				GasMixture pulled = default;
				float pulledHeatWeight = 0f;
				foreach (Vector2I intake in intakes)
				{
					List<(Vector2I cell, float weight)> pullCells = GetVentPullCells(intake);
					float totalWeight = 0f;
					foreach ((Vector2I cell, float weight) in pullCells) totalWeight += weight;
					if (totalWeight <= 0f) continue;

					foreach ((Vector2I cell, float weight) in pullCells)
					{
						_density.TryGetValue(cell, out GasMixture mix);
						if (mix.Total <= 0f) continue;

						GasMixture portion = mix * (VentFlowRate * (weight / totalWeight));
						if (portion.Total <= 0f) continue;

						pulled += portion;
						pulledHeatWeight += portion.Total * mix.Temperature;
						Accumulate(_density, cell, portion * -1f);
					}
				}

				if (pulled.Total > 0f)
				{
					pulled.Temperature = pulledHeatWeight / pulled.Total;
					int travelTicks = Mathf.Max(1, Mathf.RoundToInt(_pipeNetworkLength[n] / Mathf.Max(0.01f, PipeTravelTilesPerTick)));
					_pipeNetworkTransit[n].Enqueue((pulled, travelTicks));
				}
			}

			// 2. Age every parcel already in transit — first bleeding off a
			// share to any leak cells this network has (a parcel crossing
			// several leaky tiles over a multi-tick trip can lose most of
			// itself before it ever arrives), then releasing anything that
			// finished its trip out through the live Output vents.
			List<Vector2I> leakCells = _pipeNetworkLeakCells[n];
			Queue<(GasMixture Mixture, int TicksRemaining)> transit = _pipeNetworkTransit[n];
			int inTransitCount = transit.Count;
			for (int i = 0; i < inTransitCount; i++)
			{
				(GasMixture mixture, int ticksRemaining) = transit.Dequeue();

				if (leakCells.Count > 0)
				{
					GasMixture leaked = mixture * PipeLeakRate;
					if (leaked.Total > 0f)
					{
						mixture -= leaked;
						LeakIntoRoom(leakCells, leaked);
					}
				}

				ticksRemaining--;
				if (ticksRemaining > 0 && mixture.Total > 0f)
				{
					transit.Enqueue((mixture, ticksRemaining));
					continue;
				}

				if (outputs.Count == 0 || mixture.Total <= 0f) continue; // nowhere to go — dropped
				ReleaseToOutputs(outputs, mixture);
			}
		}
	}

	// A leak isn't suction like a vent — it just seeps directly into
	// whichever tile the damaged pipe cell itself occupies, split evenly
	// across every leak cell in the network (not distance-weighted; a
	// single small gap doesn't reach across the room the way a vent's
	// pull radius does).
	private void LeakIntoRoom(List<Vector2I> leakCells, GasMixture leaked)
	{
		GasMixture share = leaked * (1f / leakCells.Count);
		foreach (Vector2I cell in leakCells)
		{
			if (IsSolid(cell)) continue;

			_density.TryGetValue(cell, out GasMixture existing);
			float blendedTemp = BlendTemperature(existing.Temperature, existing.Total, leaked.Temperature, share.Total);

			GasMixture updated = Accumulate(_density, cell, share);
			updated.Temperature = blendedTemp;
			_density[cell] = updated.ClampedTo(MaxPressure);
		}
	}

	private void ReleaseToOutputs(List<Vector2I> outputs, GasMixture mixture)
	{
		GasMixture sharePerVent = mixture * (1f / outputs.Count);
		foreach (Vector2I output in outputs)
		{
			List<(Vector2I cell, float weight)> pullCells = GetVentPullCells(output);
			float totalWeight = 0f;
			foreach ((Vector2I cell, float weight) in pullCells) totalWeight += weight;
			if (totalWeight <= 0f) continue;

			foreach ((Vector2I cell, float weight) in pullCells)
			{
				GasMixture portion = sharePerVent * (weight / totalWeight);
				if (portion.Total <= 0f) continue;

				_density.TryGetValue(cell, out GasMixture existing);
				float blendedTemp = BlendTemperature(existing.Temperature, existing.Total, mixture.Temperature, portion.Total);

				GasMixture updated = Accumulate(_density, cell, portion);
				updated.Temperature = blendedTemp;
				_density[cell] = updated.ClampedTo(MaxPressure);
			}
		}
	}
}
