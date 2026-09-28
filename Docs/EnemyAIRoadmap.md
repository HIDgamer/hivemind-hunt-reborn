# Enemy AI Overhaul Roadmap

## Role matrix (target identity)

| Enemy | Role | Threat profile |
|---|---|---|
| Runner | Day-to-day fodder | Fast, light, dumb — dies in one or two hits, dangerous only in numbers or ambush |
| Crusher | Boss 1, later recurring mini-boss | Heavy melee brute; first real skill check, later a "serious common enemy" |
| Queen | Final boss | Ranged (acid spit) + melee (headbutt), phase-based |
| Lucy | Hardcore final boss (hardest fight in the game) | Queen's fight, but genuinely harder — not just bigger numbers |

## Current-state audit (why this roadmap starts here)

Confirmed by direct reading of `Code/Enemies/*.gd` before writing this:

- All four share `EnemyBase.gd`: health/hurtbox/contact-area wiring, voice playback, and the Mario-style stomp-kill.
- **`can_be_stomped` (`EnemyBase.gd:23`) is exported but never toggled by any subclass**, despite the base class's own comment inviting it ("a boss only stompable while stunned after a whiffed attack" — `EnemyBase.gd:163`). Crusher and Queen already have a whiff→`STUNNED` window (`Crusher.gd` CHARGE→STUNNED, `Queen.gd` HEADBUTT→STUNNED) but a player can currently stomp *or* out-damage either boss at literally any point in the fight — the "reward good positioning, punish a whiffed attack" rhythm the comments describe isn't actually enforced yet. This is the single biggest lever for making these fights read as real boss fights instead of damage races.
- No music system exists beyond a single `AudioStreamPlayer` with `autoplay=true` per level scene (see `MainMenu.tscn`). There's no crossfade, no "combat music" concept, nothing an enemy can trigger.
- No boss health bar UI exists (`SamHUD` only tracks Sam's own health/stamina).
- No "arena" concept — nothing stops the player from just running away from Queen/Lucy mid-fight, which flattens any phase/timing design.
- `Lucy.gd` is a pure reskin of `Queen.gd` via GDScript inheritance (`extends "res://Code/Enemies/Queen.gd"`) plus a stat retune and a "crit flash" cosmetic on hit. Zero unique mechanics today — everything that makes her "the hardest boss" has to be designed from scratch.
- Runner/Crusher just had their per-frame contract bug fixed this session (both now correctly re-acquire `_player` and respect multiplayer authority via `_enemy_base_physics_process`) — no deeper behavior beyond distance/sound aggro exists yet.

## Phase 0 — Shared infrastructure (blocks every later phase)

Nothing below pays off until these exist, so they come first regardless of which enemy is worked on next.

1. **Wire up the vulnerability window.** Add a protected `_invulnerable` (or reuse `can_be_stomped` directly) toggle in `EnemyBase.gd`, and have `Crusher.gd`/`Queen.gd`/`Lucy.gd` set it false while `CHARGE`/`HEADBUTT`/`SPIT` are active and true during `STUNNED` (and, for Queen/Lucy, always true — she has no armor phase, just a whiff-punish window). Gate the *damage* path the same way `_on_hurtbox_body_entered` already gates the stomp, not just the stomp itself — right now a ranged/AoE hit (a laser turret, an environmental hazard) can still chip a boss down during an attack animation even with the stomp gated, since `HealthComponent.Damage()` has no state awareness at all.
2. **Weak-point hitbox, not just a state flag.** For Crusher/Queen/Lucy, add a second small `Area2D` ("WeakPoint") only `monitorable` while vulnerable, dealing bonus damage (e.g. `Damage(amount * critMultiplier, ...)`) — this is what actually lets "timed attacks, weak points" read as a real mechanic in-game rather than a full-body damage-sponge with an invisible on/off switch. `HealthComponent.Damage`'s existing `DamageType` enum (`Code/Characters/HealthComponent.cs`) is the natural place to add a `Weakpoint` type if a distinct visual/audio hit-reaction is wanted.
3. **`MusicManager` autoload** (new, `Code/Systems/MusicManager.cs` or `.gd`, registered in `project.godot`'s `[autoload]` alongside the existing `PauseMenu`/`ChatBox`/etc.): two `AudioStreamPlayer`s crossfaded between "ambient" and "combat" tracks. Expose `EnterCombat(AudioStream track)` / `ExitCombat()`. `EnemyBase.gd` calls `EnterCombat` the moment any enemy's state machine leaves its idle/patrol state within earshot of the player, `ExitCombat` when nothing is aggroed — trivial for Runner/Crusher, but for Queen/Lucy this becomes "boss music," which should instead be triggered explicitly by an arena trigger (see next point) so it doesn't cut in/out with her own approach-tick logic.
4. **Boss arena volume.** A simple `Area2D` (`BossArenaTrigger.cs`, following the exact `Checkpoint.cs`/`LevelExitDoor.cs` convention: layer=0/mask=2, `body is Sam`, one-shot) placed around the Queen/Lucy room: on entry, locks the camera to the arena bounds (Godot `Camera2D.LimitLeft/Right/Top/Bottom`), starts boss music via `MusicManager`, and shows the boss health bar. On the boss's `Died` signal, unlocks the camera and stops the music. This is what makes "boss music" and "boss health bar" actually land as a moment rather than ambient noise.
5. **Boss health bar widget** (new `Scenes/UI/BossHealthBar.tscn` + a small script) — a `SamHUD`-adjacent but separate widget (bosses aren't the player), shown only while the arena trigger is active, bound to the boss's `HealthComponent.HealthChanged` signal. Reuse the frame-slice/`AtlasTexture` technique already established for `SamHUD`'s health/stamina bars rather than inventing a new rendering approach.

## Phase 1 — Runner: day-to-day fodder tuning

Runner's blind sound-based aggro (`Runner.gd`) is already the right shape for "dumb" — this phase is about making sure it *reads* as cannon fodder rather than just a smaller boss:

- Confirm/tune `HealthComponent.MaxHealth` on `Runner.tscn` down to a genuine "1-2 hits" pool (check current value — if it's anywhere near Crusher's, that's the first fix).
- No weak point, no phases — Runner should never need Phase 0's vulnerability gating at all; it stays a full-time damage sponge with a tiny health pool, which is the correct "fodder" feel.
- Consider a **pack/ambush variant**: 2-3 Runners sharing a wake radius (one alerted wakes nearby dormant Runners within some radius) so a single noisy mistake can pull a small swarm — cheap to add (`_alert` already exists, just needs to also ping siblings in a group) and is what makes "day-to-day enemy" feel like a real threat in numbers even though each one is trivial alone.
- No music/arena changes — Runner encounters should stay ambient, not combat-music triggers, reinforcing that boss music is special.

## Phase 2 — Crusher: Boss 1, then recurring mini-boss

Crusher already has the right skeleton (`CHARGE`→whiff→`STUNNED`, `STOMP` AOE) — this phase is about making the *first* encounter feel authored and distinct from its later "common enemy" reuse:

- Apply Phase 0's vulnerability gating + weak-point hitbox — Crusher becomes the first place the player learns "you punish the whiff, you don't just tank hits."
- **Boss 1 encounter**: its own arena trigger + boss health bar + intro beat (a brief telegraphed roar/charge-up before the fight starts, reusing `_play_voice(alert_sound)`), and its own boss track via `MusicManager`.
- **Mini-boss/common-enemy split**: rather than two separate scripts, add an `[Export] bool IsMiniBoss` (or infer from whether an arena trigger is present in the scene) that gates whether the arena/music/health-bar hookup fires at all — a later-level Crusher dropped into a normal room without an arena trigger just plays its state machine normally, no boss trappings, which is exactly "becomes a day-to-day enemy/mini-boss later" without maintaining two scripts.
- Consider a small late-game buff variant (higher `charge_speed`/`stomp_attack_damage` via scene-level export overrides only, no new code) for the mini-boss reuse to still feel like it matters showing up again.

## Phase 3 — Queen: final boss

Queen's headbutt/spit/phase-2 split already exists (`Queen.gd`) — this phase adds the depth the user is asking for on top of that skeleton:

- Vulnerability gating + weak-point hitbox from Phase 0 (her core "punish the whiff" loop).
- **Timed attacks**: her existing `headbutt_telegraph`/`spit_telegraph` windows are the hook — extend with a genuine third "AoE slam" or "spit volley" attack gated to phase 2 only, so phase 2 isn't just "the same two moves faster" (currently `phase_2_telegraph_scale`/`phase_2_cooldown_scale` only compress existing timings — add at least one phase-2-exclusive move for real variety).
- **Boss arena + music + health bar** from Phase 0, triggered on room entry.
- **Death sequence**: a proper multi-second death beat (already has `death_sound`/`Dead` animation via `EnemyBase._on_self_died`) — worth extending into a short scripted moment (screen shake, music fade-out via `MusicManager.ExitCombat`, delay before `queue_free`) rather than an instant despawn, since this is the game's final boss.

## Phase 4 — Lucy: hardcore final boss (needs the most new design work)

This is where the roadmap has the least existing code to lean on — Lucy is currently 100% Queen's mechanics. To genuinely earn "hardest boss of all" rather than "Queen with a bigger health bar," she needs at least one mechanic Queen doesn't have. Candidates (pick 1-2, don't stack all of them):

- **A third phase** (past Queen's single phase-2 split) with its own exclusive attack, since Queen tops out at 2 phases.
- **An unpunishable-unless-you-use-the-weak-point mechanic**: e.g. her whiff-stun window is shorter than Queen's (harder timing), but her `WeakPoint` hitbox (Phase 0) deals disproportionately more damage there — rewarding precision over attrition, which is a real difficulty axis beyond "more HP/damage."
- **Adds/environmental pressure during the fight**: e.g. she periodically calls in 1-2 Runners (reusing Phase 1's pack-alert hook) so the player has to manage the boss and fodder simultaneously — this is a mechanic Queen structurally can't have without becoming Lucy.
- Her own boss track (distinct from Queen's) via `MusicManager`, and her own arena trigger — sharing Queen's room asset is fine, but the encounter itself should feel authored as a distinct fight, not a recolor with a scarier number.

## Suggested build order

1. Phase 0 in full (nothing else pays off without it).
2. Phase 2 (Crusher) — smallest, most-contained boss rework, good first real test of the vulnerability-gating + arena + music infrastructure end to end.
3. Phase 1 (Runner) — cheap, independent, can slot in anytime.
4. Phase 3 (Queen).
5. Phase 4 (Lucy) — deliberately last, since her design should react to how Queen's fight actually plays once built, not be guessed at in parallel.

## Verification approach (no automated test suite — manual, in-editor)

- Phase 0: confirm a Crusher/Queen mid-charge can no longer be stomped or shot down; confirm it *can* be stomped/weak-pointed during its stun window; confirm combat music starts/stops correctly entering/leaving Queen's arena; confirm the boss health bar tracks her `HealthChanged` signal live.
- Each boss phase: full solo playthrough of that fight start-to-finish, specifically trying to "cheese" it by attacking outside the punish window (should fail) and by using the whiff window (should work).
- Lucy: confirm her fight is measurably harder than Queen's in an actual timed playtest, not just on paper stat comparison.
