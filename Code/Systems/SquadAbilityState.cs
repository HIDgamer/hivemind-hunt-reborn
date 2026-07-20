using Godot;

// Autoload. Ability pickups in this project are explicitly squad-wide (see
// AbilityPickupComponent's own header comment: "one player grabbing dash
// unlocks dash for everyone") — but nothing actually remembered that after
// the fact. Pickups are static per-scene Area2Ds with zero persistence and
// zero multiplayer sync; a player who disconnects and rejoins reloads the
// whole level fresh, getting a brand-new un-consumed pickup node whose
// server-side counterpart was already freed back when it was first
// collected — permanently inert (visible, "interactable," does nothing).
//
// This autoload is the missing memory: every peer records what's been
// unlocked so far this hosted session, a freshly-loaded pickup can check
// "has someone already gotten this?" in its own _Ready() instead of only
// ever finding out via an RPC that will never arrive again, and any newly
// spawned/rejoined Sam gets everything the squad has already earned.
//
// In-memory only, scoped to "this hosted session" (Reset() is called from
// NetworkManager.StartHost()) — not meant to survive the process
// restarting, just to survive an individual player's own disconnect and
// reconnect within the same session. Applied unconditionally, not gated to
// networked play — a single-player death-before-checkpoint scene reload
// hits this exact same underlying bug (abilities silently reset), so there
// is no reason to special-case it away from single-player.
public partial class SquadAbilityState : Node
{
	public bool ExtraJumpUnlocked { get; private set; }
	public int ExtraJumpCount { get; private set; }
	public bool DashUnlocked { get; private set; }

	// MaxHealth pickups are cumulative (IncreaseMaxHealth is additive), not
	// a single unlock flag like the other two — but there's exactly one
	// health pickup in the game right now, so MaxHealthCollected doubles as
	// "the one pickup that exists has been taken" for self-consume purposes.
	// If a second, distinct health pickup is ever added, this assumption
	// needs revisiting (both would self-consume together).
	public bool MaxHealthCollected { get; private set; }
	public int MaxHealthBonus { get; private set; }

	public void Reset()
	{
		ExtraJumpUnlocked = false;
		ExtraJumpCount = 0;
		DashUnlocked = false;
		MaxHealthCollected = false;
		MaxHealthBonus = 0;
	}

	public void RecordAbility(AbilityPickupComponent.AbilityKind kind, int amount)
	{
		switch (kind)
		{
			case AbilityPickupComponent.AbilityKind.ExtraJump:
				ExtraJumpUnlocked = true;
				// UnlockExtraJumps replaces capacity rather than adding to
				// it, so the squad's record tracks the highest grant ever
				// made, not a running sum.
				ExtraJumpCount = Mathf.Max(ExtraJumpCount, amount);
				break;
			case AbilityPickupComponent.AbilityKind.Dash:
				DashUnlocked = true;
				break;
			case AbilityPickupComponent.AbilityKind.MaxHealth:
				MaxHealthCollected = true;
				MaxHealthBonus += amount;
				break;
		}
	}

	public bool IsUnlocked(AbilityPickupComponent.AbilityKind kind) => kind switch
	{
		AbilityPickupComponent.AbilityKind.ExtraJump => ExtraJumpUnlocked,
		AbilityPickupComponent.AbilityKind.Dash => DashUnlocked,
		AbilityPickupComponent.AbilityKind.MaxHealth => MaxHealthCollected,
		_ => false,
	};

	// Applies everything the squad has earned so far to a newly spawned or
	// rejoined Sam — the exact same grant calls AbilityPickupComponent's own
	// GrantTo already makes, just driven from recorded totals instead of a
	// single pickup's own export fields.
	public void ApplyAll(Sam player)
	{
		if (ExtraJumpUnlocked)
		{
			player.GetNodeOrNull<ExtraJumpComponent>("ExtraJumpComponent")?.UnlockExtraJumps(ExtraJumpCount);
		}

		if (DashUnlocked)
		{
			DashComponent dash = player.GetNodeOrNull<DashComponent>("DashComponent");
			if (dash == null)
			{
				dash = new DashComponent { Name = "DashComponent" };
				player.AddChild(dash);
			}
			dash.UnlockDash();
		}

		if (MaxHealthBonus > 0)
		{
			player.GetNodeOrNull<HealthComponent>("HealthComponent")?.IncreaseMaxHealth(MaxHealthBonus);
		}
	}
}
