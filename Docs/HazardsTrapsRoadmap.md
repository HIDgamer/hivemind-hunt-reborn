# Hazards & Traps Roadmap

## Current-state audit (grounding)

Confirmed by direct reading of every hazard-adjacent script before writing this — no folder guessing:

- **There is no shared hazard base class** (unlike `EnemyBase.gd`/`TaskStationBase.cs`). The project's existing strategy is component + scene composition instead: `TimedHazardEmitter.cs` (a generic cyclic-hazard component reused across scene variants), `FlickeringLight.cs` (a `Profile` enum covering several lighting looks), `StatusEffectComponent.cs` (generic status application), `SparkHazard.cs` (a bare reusable damage-zone component). New trap *types* are meant to be new scenes assembling these, not new scripts each time — a real, deliberate precedent worth keeping, not something to abandon in favor of a big inheritance tree.
- **Capability matrix is fragmented** — no hazard has the full set of "can be powered off, telegraphs before hurting you, can be broken/repaired":

  | Hazard | `Powered(bool)` | Telegraph before damage | `Break()`/`Repair()` |
  |---|---|---|---|
  | `GasVent.cs` | Yes | None (Emitter role) | Break + Repair |
  | `LaserTurret.cs` | Yes | Visual only (sight-cone color) | Neither |
  | `Laser.cs` | No | Full Warning→Active cycle | Neither |
  | `TimedHazardEmitter.cs` | No | Full Telegraphing→Bursting cycle | Break only, no Repair |
  | `SparkHazard.cs` | No | None — always-on | Neither |
  | `FireEmitterHazard.cs` | No | None — always-on | Neither |

  Concretely: **`SparkHazard` and `FireEmitterHazard` can't be wired to a power-reroute puzzle or a lever at all** — they're always live for as long as they exist in the scene. That's the single biggest gap standing between "hazard" and "trap" (a trap implies something can be defused/timed/triggered; an always-on damage zone is just scenery).
- **Duplicated boilerplate, same block copy-pasted 4 times**: the "find `HealthComponent` on the overlapping body, fall back to its parent, call `.Damage(amount, knockback)`" lookup appears near-verbatim in `SparkHazard.TryDamage`, `FireEmitterHazard.TryDamage`, `Laser.TryDamage`, and `TimedHazardEmitter.DamageOverlapping`. The `_timeSinceLastDamage`/`DamageCooldown` tick-gate is duplicated identically between `SparkHazard` and `FireEmitterHazard`. `IsBroken`/`Break()`/the `"ExplodableVent"` group membership is duplicated between `GasVent` and `TimedHazardEmitter`.
- **Terrain-scorch inconsistency**: `Laser.cs` calls `EnvironmentDamage.ApplyScorch(...)` every tick it hits a surface, and `GasSimulation`'s explosions scorch *and* structurally destroy terrain — but `FireEmitterHazard.cs` never touches `EnvironmentDamage` at all despite being a fire source. Standing a fire emitter next to a wall doesn't char it, which reads as an inconsistency the moment a player's seen a laser do exactly that.
- **`SparkConduit.tscn`'s "spark burst" is purely cosmetic** — it's `FlickeringLight.cs` (Profile=SparkBurst, a periodic light-flash/audio-crackle) layered over a `SparkHazard` child that's *continuously* live. The flash looks like the dangerous moment; the hitbox doesn't agree — it's dangerous 100% of the time. This is a fairness/readability bug hiding in plain sight, not a design choice.
- **No classic platformer "trap" props exist yet at all** — nothing like a spike pit, a swinging blade/saw, a falling/crushing block, or an ambush trigger (pressure-plate-activated ceiling drop). Everything surveyed is an *environmental* hazard (gas, fire, electricity, lasers) rather than a *trap* in the genre sense. `PressurePlateComponent.cs` already exists and drives `Powered(bool)` targets (per `Door.cs`'s own comment) — it's a ready-made trigger for weight-activated traps, just nothing traps into it yet besides doors/lights.
- `Door3.cs` (built this session — a trap door that starts open and seals permanently once a player passes through) is the first genuine "trap" in this sense, and a useful reference point: a hazard/prop whose whole identity is a one-way, player-triggered state change rather than an ambient danger.

## Phase 0 — Shared infrastructure (blocks every later phase from feeling consistent)

1. **`HazardDamageUtil` static helper** (new, `Code/Props/HazardDamageUtil.cs`): the body-then-parent `HealthComponent` lookup + `Damage(amount, knockback)` call, extracted once. `SparkHazard`, `FireEmitterHazard`, `Laser`, `TimedHazardEmitter` all switch to calling it instead of their own copy. Pure deduplication, zero behavior change — the safest possible first step and a good build-verification checkpoint before touching anything riskier.
2. **A shared `TickDamageArea` component** for the "`Area2D`, poll overlapping bodies every `_Process`, damage-tick on a cooldown" shape duplicated in `SparkHazard`/`FireEmitterHazard`. Same spirit as `TimedHazardEmitter` but for the *simple* always-on case.
3. **Unify the `Powered(bool)` contract** — give `SparkHazard`, `FireEmitterHazard`, and `TimedHazardEmitter` the same method (trivial: `Powered(false)` just sets `SetProcess(false)`/disables the damage area; `Powered(true)` reverses it). This alone turns three previously-always-on hazards into things a level designer can wire to a lever, a `PressurePlateComponent`, or a power-reroute puzzle — directly enabling "trap that's only live when X" level design that's currently impossible.
4. **Give `TimedHazardEmitter` a `Repair()`** symmetric with `GasVent`'s, so a broken cyclic trap can be fixed via the same `PipePatchStation`-style flow instead of being permanently dead once exploded.
5. **Fire hazards should scorch terrain** — `FireEmitterHazard` calls `EnvironmentDamage.ApplyScorch(...)` periodically while active, matching `Laser`'s existing behavior, closing the inconsistency.
6. **Fix `SparkConduit`'s damage window** — gate the `SparkHazard` child's `Powered`/active state to the same interval `FlickeringLight`'s SparkBurst profile is mid-flash, so the hazard is only actually dangerous when it visibly looks dangerous. (Depends on item 3 above existing first.)

## Phase 1 — New classic trap props (the actual "traps" a platformer roadmap should have)

None of these exist yet; all are new scenes, reusing Phase 0's shared pieces rather than reinventing damage/telegraph logic per trap:

- **Spike trap** — a `TimedHazardEmitter`-driven variant (Idle→Telegraphing→Bursting already fits "spikes retract, warn, extend, retract" perfectly) with new spike art and a short `BurstDuration`. Almost entirely a content task once Phase 0's `Powered`/`Repair` land, not a new script.
- **Swinging blade / saw** — genuinely new: a `Node2D` pendulum (simple sinusoidal or `AnimatableBody2D` rotation) with a `HazardDamageUtil`-based hitbox on the blade itself. No telegraph needed (a visibly swinging blade *is* its own telegraph) — the interesting design space is the swing period/arc, not a state machine.
- **Falling/crushing block** — starts dormant above a marked drop zone, triggered by the player entering a `PressurePlateComponent`-style zone *underneath* or *just before* it (a "you hear it creak, then it drops" telegraph reusing `TimedHazardEmitter`'s Telegraphing phase for the creak/shake), then falls and deals a one-time impact hit via `HazardDamageUtil` before resetting (or staying broken — designer's choice per instance, same as `GasVent`'s `Break()`/`Repair()` split).
- **Ambush trigger trap** — a `PressurePlateComponent` (already exists) wired to something *other* than a door/light for the first time: spawn a burst of `LaserBolt`s, drop a `Falling/crushing block` (above), or momentarily arm a normally-`Powered(false)` `SparkHazard`. This is pure level-design composition once Phase 0's `Powered` unification lands — no new code at all, just the first level actually using the pattern.

## Phase 2 — Combo/synergy traps

Once Phase 0 + 1 exist, the more interesting design space is hazards that interact with each other and with existing systems rather than being isolated props:

- **Gas + ignition trap**: a `GasVent` (Emitter) filling a room, paired with a `SparkHazard`/spike trap the player must avoid triggering while gas is present — `GasSimulation.TryIgnite` already exists and already chains into `DetonateExplosion`; this phase is purely about deliberately *placing* emitter + ignition source together as an authored trap instead of the interaction only ever happening by accident.
- **Trap doors as a category** (`Door3.cs`, already built): more variants worth considering once the base one is proven in a real level — e.g. a trap door that closes on a *timer* rather than on player-crossing, or one a `PressurePlateComponent` elsewhere in the room can slam shut remotely.
- **Weak-point crossover with the Enemy AI roadmap**: `HazardDamageUtil`'s existence makes it trivial for a hazard to deal bonus damage to an enemy's `WeakPoint` node the same way a stomp does (see `Docs/EnemyAIRoadmap.md`'s Phase 0) — e.g. a spike trap an enemy can be lured/knocked onto. Not urgent, but worth keeping in mind so the two roadmaps' damage plumbing stays compatible rather than diverging.

## Suggested build order

1. Phase 0 in full — it's small, safe (item 1 is pure deduplication with no behavior change), and every later trap either depends on `Powered()` existing uniformly or benefits from not duplicating damage/cooldown logic a fourth and fifth time.
2. Phase 1's spike trap first (cheapest — mostly content, reuses `TimedHazardEmitter` directly).
3. Phase 1's swinging blade (genuinely new but self-contained, no dependencies on other traps).
4. Phase 1's falling block + ambush trigger together (the falling block IS the payload the ambush trigger fires).
5. Phase 2 last, once there's more than one trap type actually in a level to combine.

## Verification approach (no automated test suite — manual, in-editor)

- Phase 0 item 1 (dedup): confirm every hazard that used the old inline lookup still damages the player identically after switching to `HazardDamageUtil` — a pure refactor, should be behaviorally silent.
- Phase 0 item 3 (`Powered`): wire a `SparkHazard`/`FireEmitterHazard` to a `PowerRerouteStation` or lever and confirm it can actually be turned off/on, which is impossible today.
- Phase 0 item 6 (SparkConduit fairness): stand next to one for a full flash cycle and confirm damage only lands during the visible spark, not during the dark interval.
- Each new Phase 1 trap: solo playthrough specifically trying to get hit by it (confirm the telegraph gives a fair reaction window) and specifically trying to avoid it (confirm it doesn't hit through a fair dodge).
